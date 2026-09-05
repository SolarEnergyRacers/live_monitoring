SELECT * FROM GpsPoints where DeviceName = 'cat' ORDER BY "Timestamp" DESC LIMIT 16;

SELECT * FROM GpsPoints;

---- m/s -> km/h
-- UPDATE GpsPoints SET SpeedKmh = SpeedKmh * 3.6 where DeviceName = 'honor'

--- counts

SELECT count(*) FROM GpsPoints WHERE DeviceName='cat' OR DeviceName = 'honor'