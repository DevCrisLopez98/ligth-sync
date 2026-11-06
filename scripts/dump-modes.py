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


def parse_mode(reader):
    mode = {"name": reader.text()}
    mode["flags"] = reader.u32()
    mode["speed_min"] = reader.u32()
    mode["speed_max"] = reader.u32()
    mode["brightness_min"] = reader.u32()
    mode["brightness_max"] = reader.u32()
    mode["colors_min"] = reader.u32()
    mode["colors_max"] = reader.u32()
    mode["speed"] = reader.u32()
    mode["brightness"] = reader.u32()
    mode["direction"] = reader.u32()
    mode["color_mode"] = reader.u32()
    count = reader.u16()
    mode["colors"] = [reader.u32() for _ in range(count)]
    return mode


def parse_zone(reader):
    name = reader.text()
    reader.i32()
    reader.u32()
    reader.u32()
    reader.u32()
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
    modes = [parse_mode(reader) for _ in range(reader.u16())]
    reader.text()
    return name, modes


def parse_device(data):
    reader = Reader(data)
    reader.u32()
    reader.i32()
    name = reader.text()
    for _ in range(5):
        reader.text()
    mode_count = reader.u16()
    active = reader.i32()
    modes = [parse_mode(reader) for _ in range(mode_count)]
    zones = [parse_zone(reader) for _ in range(reader.u16())]
    return name, active, modes, zones


def main():
    sock = socket.create_connection((HOST, PORT), 5)
    version = struct.unpack("<I", request(sock, 0, 40, struct.pack("<I", PROTOCOL), 40))[0]
    print("protocol", version)
    send_packet(sock, 0, 50, b"LightSyncDump\0")
    count_data = request(sock, 0, 0, b"", 0)
    count = struct.unpack_from("<I", count_data)[0]
    ids = list(struct.unpack_from("<" + ("I" * count), count_data, 4)) if count else []
    print("devices", count)
    for dev_id in ids:
        name, active, modes, zones = parse_device(request(sock, dev_id, 1, struct.pack("<I", PROTOCOL), 1))
        print(f"--- {dev_id} {name} active={active}")
        for index, mode in enumerate(modes):
            print(
                f"  mode[{index}] {mode['name']!r} flags=0x{mode['flags']:X} "
                f"colors={mode['colors_min']}-{mode['colors_max']} color_mode={mode['color_mode']}"
            )
        for zone_name, zone_modes in zones:
            if not zone_modes:
                continue
            print(f"  zone {zone_name}:")
            for index, mode in enumerate(zone_modes):
                print(
                    f"    zmode[{index}] {mode['name']!r} flags=0x{mode['flags']:X} "
                    f"colors={mode['colors_min']}-{mode['colors_max']}"
                )


if __name__ == "__main__":
    main()
