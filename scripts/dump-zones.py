import socket
import struct

HOST = "127.0.0.1"
PORT = 6742
PROTOCOL = 6


def recvall(sock, size):
    data = b""
    while len(data) < size:
        chunk = sock.recv(size - len(data))
        if not chunk:
            raise ConnectionError("closed")
        data += chunk
    return data


def send_packet(sock, dev_id, pkt_id, payload=b""):
    sock.sendall(b"ORGB" + struct.pack("<III", dev_id, pkt_id, len(payload)) + payload)


def read_packet(sock):
    magic, dev_id, pkt_id, size = struct.unpack("<4sIII", recvall(sock, 16))
    return dev_id, pkt_id, recvall(sock, size)


def request(sock, dev_id, pkt_id, payload=b"", expect=None):
    send_packet(sock, dev_id, pkt_id, payload)
    while True:
        _, resp_id, data = read_packet(sock)
        if resp_id == 10:
            continue
        if expect is None or resp_id == expect:
            return data


class Reader:
    def __init__(self, data):
        self.data = data
        self.pos = 0

    def take(self, size):
        self.pos += size

    def u16(self):
        value = struct.unpack_from("<H", self.data, self.pos)[0]
        self.pos += 2
        return value

    def u32(self):
        value = struct.unpack_from("<I", self.data, self.pos)[0]
        self.pos += 4
        return value

    def i32(self):
        value = struct.unpack_from("<i", self.data, self.pos)[0]
        self.pos += 4
        return value

    def text(self):
        size = self.u16()
        raw = self.data[self.pos:self.pos + size]
        self.pos += size
        return raw.split(b"\0", 1)[0].decode("utf-8", "replace")


def skip_mode(reader):
    reader.text()
    reader.take(44)
    reader.take(reader.u16() * 4)


def parse_zone(reader):
    name = reader.text()
    reader.i32()
    reader.u32()
    leds_max = reader.u32()
    leds_count = reader.u32()
    reader.take(reader.u16())
    for _ in range(reader.u16()):
        reader.text()
        reader.i32()
        reader.u32()
        reader.u32()
        reader.take(reader.u16())
        reader.u32()
    reader.u32()
    reader.i32()
    for _ in range(reader.u16()):
        skip_mode(reader)
    reader.text()
    return name, leds_count, leds_max


def main():
    sock = socket.create_connection((HOST, PORT), 5)
    request(sock, 0, 40, struct.pack("<I", PROTOCOL), 40)
    send_packet(sock, 0, 50, b"LightSyncZones\0")
    count_data = request(sock, 0, 0, b"", 0)
    count = struct.unpack_from("<I", count_data)[0]
    ids = list(struct.unpack_from("<" + ("I" * count), count_data, 4)) if count else []
    for dev_id in ids:
        reader = Reader(request(sock, dev_id, 1, struct.pack("<I", PROTOCOL), 1))
        reader.u32()
        reader.i32()
        name = reader.text()
        for _ in range(5):
            reader.text()
        mode_count = reader.u16()
        reader.i32()
        for _ in range(mode_count):
            skip_mode(reader)
        print(f"--- {name}")
        for _ in range(reader.u16()):
            zone_name, leds, leds_max = parse_zone(reader)
            print(f"  {zone_name}: {leds}/{leds_max}")


if __name__ == "__main__":
    main()
