#!/usr/bin/env python3
"""Derive the minimum sample against the interval band 1 actually computes.

A one-time derivation aid on the footing of derive-indicators.py and derive-authored-parameters.py,
and not run by CI. It restates PairedInterval.Of and PairedInterval.Disperse in Python, measures
the paired dispersion over the flagged population of the calibration store, and finds by simulation
the shortest series at which the studentised moving-block bootstrap detects a two-point difference
with 90% power. The specification it implements is the 5.0(b) PROGRESS entry of 2026-09-03 titled
"the minimum sample's derivation, specified before it runs"; every input it takes is named there.

Usage: python tools/derive-minimum-sample.py [--store data/live/pullbackstrategylab.db]
                                             [--series 1000] [--draws 10000] [--seed 20260903]
"""

from __future__ import annotations

import argparse
import math
import sqlite3
from collections import defaultdict

import numpy as np

Z_ALPHA = 1.959964
Z_BETA = 1.281552
DELTA = 0.02
CONTROLS_PER_SET = 5
BLOCK = 10
MINIMUM_SESSIONS = 2 * BLOCK
DISPERSION_MINIMUM_NAMES = 20
HORIZON = 10

# The clustering, from the reconstructed read of 2026-08-31 (PROGRESS, 3.3 of that date), loose
# panels at sixty sessions: rows / nights, design effect, across-night discount.
CLUSTERING = {
    "long": {"pairs": 4824 / 50, "design": 3.40, "serial": 0.3718},
    "short": {"pairs": 3017 / 50, "design": 2.80, "serial": 0.2677},
}


def forward_returns(store: str, side: str) -> dict[str, list[float]]:
    """Ten-session signed forward returns of the flagged rows, per session, on the store's bars."""
    c = sqlite3.connect(f"file:{store}?mode=ro", uri=True)
    rows = c.execute(
        "SELECT as_of, ticker FROM calibration_setup WHERE direction = ? ORDER BY as_of, ticker", (side,)
    ).fetchall()
    tickers = sorted({t for _, t in rows})
    closes: dict[str, tuple[list[str], list[float]]] = {}
    for ticker in tickers:
        bars = c.execute(
            "SELECT bar_date, adj_close FROM daily_bar WHERE ticker = ? ORDER BY bar_date", (ticker,)
        ).fetchall()
        closes[ticker] = ([d for d, _ in bars], [float(x) for _, x in bars])
    sign = 1.0 if side == "long" else -1.0
    per_session: dict[str, list[float]] = defaultdict(list)
    dropped = 0
    for as_of, ticker in rows:
        dates, values = closes[ticker]
        try:
            i = dates.index(as_of)
        except ValueError:
            dropped += 1
            continue
        if i + HORIZON >= len(dates) or values[i] == 0.0:
            dropped += 1
            continue
        per_session[as_of].append(sign * (values[i + HORIZON] / values[i] - 1.0))
    print(f"{side}: {len(rows):,} rows, {dropped:,} dropped for want of a tenth session or a bar")
    return per_session


def dispersion(per_session: dict[str, list[float]]) -> tuple[float, float, int, int]:
    """ForwardDispersion.Of restated: pooled residual variance by degrees of freedom."""
    sum_squares = 0.0
    dof = 0
    used = 0
    observations = 0
    for _, returns in sorted(per_session.items()):
        if len(returns) < DISPERSION_MINIMUM_NAMES:
            continue
        mean = sum(returns) / len(returns)
        sum_squares += sum((r - mean) ** 2 for r in returns)
        dof += len(returns) - 1
        observations += len(returns)
        used += 1
    idiosyncratic = round(math.sqrt(sum_squares / dof), 6)
    paired = round(idiosyncratic * math.sqrt(1.0 + 1.0 / CONTROLS_PER_SET), 6)
    return idiosyncratic, paired, used, observations


def normal_theory(paired: float) -> int:
    scaled = (Z_ALPHA + Z_BETA) * paired / DELTA
    return math.ceil(scaled * scaled)


def percentile(sorted_values: np.ndarray, fraction: float) -> float:
    index = int(math.floor(fraction * (len(sorted_values) - 1)))
    return float(sorted_values[max(0, min(index, len(sorted_values) - 1))])


def interval_lower_bound(values: np.ndarray, draws: int, rng: np.random.Generator) -> float | None:
    """PairedInterval.Of restated: the studentised moving-block bootstrap's lower bound."""
    nights = len(values)
    if nights < 2 * BLOCK:
        return None
    blocks = nights // BLOCK
    if blocks < 2:
        return None
    observed = float(values.mean())
    offset = nights - blocks * BLOCK
    trailing = values[offset:].reshape(blocks, BLOCK).mean(axis=1)
    error = math.sqrt(trailing.var(ddof=1) / blocks)
    if error <= 0.0:
        return None
    starts = rng.integers(0, nights, size=(draws, blocks))
    index = (starts[:, :, None] + np.arange(BLOCK)[None, None, :]) % nights
    block_means = values[index].mean(axis=2)
    resampled = block_means.mean(axis=1)
    scale = np.sqrt(block_means.var(axis=1, ddof=1) / blocks)
    keep = scale > 0.0
    if not keep.any():
        return None
    ratios = np.sort((resampled[keep] - observed) / scale[keep])
    return observed - percentile(ratios, 0.975) * error


def effective(means: np.ndarray, pairs: np.ndarray, within: np.ndarray) -> float:
    """PairedInterval.Disperse restated: harmonic-mean rows over the design effect, times serial."""
    nights = len(means)
    rows = int(pairs.sum())
    independent = nights * nights / float((1.0 / np.maximum(1, pairs)).sum())
    centred = means - means.mean()
    sum_squares = float((centred * centred).sum())
    if sum_squares == 0.0:
        return 1.0
    rho = float((centred[1:] * centred[:-1]).sum()) / sum_squares
    serial = 1.0 if rho <= -1.0 else (1.0 - rho) / (1.0 + rho)
    serial = min(1.0, max(0.0, serial))
    observed_variance = sum_squares / (nights - 1)
    mask = pairs >= 2
    dof = int((pairs[mask] - 1).sum())
    weighted = float(((pairs[mask] - 1) * within[mask] * within[mask]).sum())
    if dof == 0 or weighted <= 0.0:
        return max(1.0, min(rows, round(nights * serial)))
    within_variance = weighted / dof
    expected = float((within_variance / pairs).mean())
    design = max(1.0, observed_variance / expected)
    return max(1.0, min(rows, round(independent / design * serial)))


def simulate(
    nights: int, paired: float, icc: float, phi: float, counts: np.ndarray, rng: np.random.Generator
) -> tuple[np.ndarray, np.ndarray, np.ndarray]:
    sigma_u = paired * math.sqrt(icc)
    sigma_e = paired * math.sqrt(1.0 - icc)
    u = np.empty(nights)
    u[0] = rng.normal(0.0, sigma_u)
    innovation = sigma_u * math.sqrt(1.0 - phi * phi)
    for t in range(1, nights):
        u[t] = phi * u[t - 1] + rng.normal(0.0, innovation)
    pairs = rng.choice(counts, size=nights)
    means = np.empty(nights)
    within = np.empty(nights)
    for t in range(nights):
        differences = DELTA + u[t] + rng.normal(0.0, sigma_e, size=int(pairs[t]))
        means[t] = differences.mean()
        within[t] = differences.std(ddof=1) if pairs[t] > 1 else 0.0
    return means, pairs.astype(float), within


def power_at(
    nights: int, paired: float, icc: float, phi: float, counts: np.ndarray, series: int, draws: int, rng
) -> tuple[float, float]:
    detected = 0
    effectives = []
    for _ in range(series):
        means, pairs, within = simulate(nights, paired, icc, phi, counts, rng)
        lower = interval_lower_bound(means, draws, rng)
        if lower is not None and lower > 0.0:
            detected += 1
        effectives.append(effective(means, pairs, within))
    return detected / series, float(np.mean(effectives))


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--store", default="data/live/pullbackstrategylab.db")
    parser.add_argument("--series", type=int, default=1000)
    parser.add_argument("--draws", type=int, default=10000)
    parser.add_argument("--seed", type=int, default=20260903)
    args = parser.parse_args()

    measured = {}
    for side in ("long", "short"):
        per_session = forward_returns(args.store, side)
        idio, paired, used, observations = dispersion(per_session)
        counts = np.array([len(v) for v in per_session.values()], dtype=float)
        measured[side] = (idio, paired, used, observations, counts)
        print(
            f"{side}: single-name dispersion {idio:.6f}, paired {paired:.6f}, over {used} sessions holding "
            f"at least {DISPERSION_MINIMUM_NAMES} names and {observations:,} observations; "
            f"normal-theory minimum at that dispersion {normal_theory(paired)}; pairs a night mean {counts.mean():.1f}"
        )

    side = max(measured, key=lambda s: measured[s][1])
    idio, paired, used, observations, counts = measured[side]
    print(f"the side used is {side}, the larger paired dispersion, {paired:.6f}")

    clustering = CLUSTERING[side]
    icc = (clustering["design"] - 1.0) / (clustering["pairs"] - 1.0)
    rho = (1.0 - clustering["serial"]) / (1.0 + clustering["serial"])
    m_bar = float(counts.mean())
    sigma_u2 = icc * paired * paired
    sigma_e2 = (1.0 - icc) * paired * paired
    phi_unclamped = rho * (sigma_u2 + sigma_e2 / m_bar) / sigma_u2
    phi = min(0.98, phi_unclamped)
    print(
        f"clustering from the record: design {clustering['design']} at {clustering['pairs']:.1f} pairs gives "
        f"ICC {icc:.5f}; serial {clustering['serial']} gives rho {rho:.4f}; phi {phi_unclamped:.4f}"
        + (" clamped to 0.98" if phi_unclamped > 0.98 else " unclamped")
    )

    rng = np.random.default_rng(args.seed)
    nights = MINIMUM_SESSIONS
    history = []
    last_below = None
    while True:
        power, eff = power_at(nights, paired, icc, phi, counts, args.series, args.draws, rng)
        history.append((nights, power, eff))
        print(f"N={nights:4d} nights: power {power:.3f}, effective observations {eff:.1f}")
        if power >= 0.90:
            break
        last_below = nights
        nights += 10
        if nights > 2000:
            raise SystemExit("power never reached 90% below 2,000 nights; the escape fires")
    coarse = nights
    if last_below is not None:
        for n in range(last_below + 1, coarse):
            power, eff = power_at(n, paired, icc, phi, counts, args.series, args.draws, rng)
            history.append((n, power, eff))
            print(f"N={n:4d} nights: power {power:.3f}, effective observations {eff:.1f}")
            if power >= 0.90:
                nights = n
                break
    n_star = nights
    power, eff = [(p, e) for (n, p, e) in history if n == n_star][-1]
    pinned = math.ceil(eff)
    nz = normal_theory(paired)
    print()
    print(f"N* = {n_star} nights at power {power:.3f}")
    print(f"dispersion correction alone, normal theory at the flagged paired dispersion: {nz}")
    print(f"both corrections, effective observations at N*: {pinned}")
    print(f"bootstrap factor: {pinned / nz:.3f}")


if __name__ == "__main__":
    main()
