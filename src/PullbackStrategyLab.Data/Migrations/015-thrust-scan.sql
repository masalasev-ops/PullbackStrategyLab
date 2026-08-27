-- 015  thrust_scan, thrust_session
--
-- Which scan produced the thrust a setup was measured against, recorded on the row.
--
-- The detector already knows this at evaluation time. It reads `scan_hit`, takes the most recent
-- qualifying hit inside the thrust window, and hands that session's index to
-- `PullbackGeometry.Of`. What it has never done is write down which of the three scans the hit
-- came from, so after the fact nothing can tell a one-session hit from a twenty-session one.
--
-- That distinction is the whole of the 3.0(c) correction. `gainer` and `gapper` flag a move over
-- one session; `leader` and `laggard` flag one over twenty. The geometry currently measures the
-- thrust from the close before the flagged session in every case, which is right for the day
-- scans and wrong for the month scans, and a run cannot be split by scan family to show it.
--
-- `thrust_scan` is a signal too, and that is not enough. `setup_signal` has a foreign key to
-- `setup`, calibration writes to `calibration_setup`, so no calibration row can carry a signal
-- and `setup_signal` is empty in the live store. The 49,450-row population the prediction is
-- judged over is exactly the population the signal cannot reach, which is why this is a column
-- on both tables rather than a second signal.
--
-- Nullable on purpose, and it is the honest shape rather than a convenience. A setup row exists
-- only if it cleared the recording floor and `thrust` is one of the four floor checks, so in
-- practice every row has a hit. But `Evidence` returns a row whenever the window supports one,
-- the floor is applied a layer above, and a name whose scan hit could not be resolved is a real
-- state the failure table already names. A NOT NULL here would make the detector invent a value
-- for it, which is the thing this column exists to stop.
--
-- No CHECK constraint listing the six scan names. The list lives in `ScanEngine.Scans` and is
-- asserted against ARCHITECTURE by the conformance check; repeating it here would put the same
-- fact in two places and the migration is the copy nobody would think to update.

ALTER TABLE setup ADD COLUMN thrust_scan TEXT NULL;
ALTER TABLE setup ADD COLUMN thrust_session TEXT NULL;

ALTER TABLE calibration_setup ADD COLUMN thrust_scan TEXT NULL;
ALTER TABLE calibration_setup ADD COLUMN thrust_session TEXT NULL;

-- The read the correction makes: a night's rows split by which scan family produced the thrust.
-- Without it, splitting 49,450 calibration rows by scan is a table scan per session.
CREATE INDEX ix_calibration_setup_thrust_scan ON calibration_setup (thrust_scan, as_of);
