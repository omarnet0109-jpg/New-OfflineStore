-- Offline Serial/COM barcode device. Does not require Internet or change stock balances.
-- Only administrators may modify it through StoreService.
CREATE TABLE serial_scanner_config (
    id INTEGER PRIMARY KEY CHECK(id=1),
    enabled INTEGER NOT NULL DEFAULT 0 CHECK(enabled IN (0,1)),
    port_name TEXT NOT NULL DEFAULT '' CHECK(length(port_name) <= 20),
    baud_rate INTEGER NOT NULL DEFAULT 9600 CHECK(baud_rate IN (1200,2400,4800,9600,19200,38400,57600,115200)),
    data_bits INTEGER NOT NULL DEFAULT 8 CHECK(data_bits IN(7,8)),
    parity TEXT NOT NULL DEFAULT 'None' CHECK(parity IN('None','Odd','Even','Mark','Space')),
    stop_bits TEXT NOT NULL DEFAULT 'One' CHECK(stop_bits IN('One','OnePointFive','Two')),
    handshake TEXT NOT NULL DEFAULT 'None' CHECK(handshake IN('None','XOnXOff','RequestToSend','RequestToSendXOnXOff')),
    updated_utc TEXT NOT NULL DEFAULT ''
);
INSERT INTO serial_scanner_config(id) VALUES(1);
