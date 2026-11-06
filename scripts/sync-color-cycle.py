import colorsys
import select
import socket
import struct
import time

HOST = "127.0.0.1"
PORT = 6742
PROTOCOL = 6
PERIOD_SECONDS = 8.0
FRAME_SECONDS = 0.033
STRIP_LEDS = 120
MODE_COLORS_MODE_SPECIFIC = 2


def recvall(sock, size):
    data = b""
    while len(data) < size:
        chunk = sock.recv(size - len(data))
        if not chunk:
            raise ConnectionError("OpenRGB closed the connection")
        data += chunk
    return data


def send_packet(sock, dev_id, pkt_id, payload=b""):
    sock.sendall(b"ORGB" + struct.pack("<III", dev_id, pkt_id, len(payload)) + payload)


def read_packet(sock):
    magic, dev_id, pkt_id, size = struct.unpack("<4sIII", recvall(sock, 16))
    if magic != b"ORGB":
        raise RuntimeError("bad OpenRGB packet")
    return dev_id, pkt_id, recvall(sock, size)


def request(sock, dev_id, pkt_id, payload=b"", expect=None):
    send_packet(sock, dev_id, pkt_id, payload)
    while True:
        _, resp_id, data = read_packet(sock)
        if resp_id == 10:
            continue
        if expect is None or resp_id == expect:
            return data


def drain(sock):
    while True:
        ready, _, _ = select.select([sock], [], [], 0)
        if not ready:
            return
        read_packet(sock)


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


def parse_segment(reader):
    reader.text()
    reader.i32()
    reader.u32()
    reader.u32()
    matrix_len = reader.u16()
    reader.take(matrix_len)
    reader.u32()


def parse_zone(reader):
    name = reader.text()
    reader.i32()
    reader.u32()
    leds_max = reader.u32()
    leds_count = reader.u32()
    reader.take(reader.u16())
    for _ in range(reader.u16()):
        parse_segment(reader)
    reader.u32()
    reader.i32()
    for _ in range(reader.u16()):
        parse_mode(reader)
    reader.text()
    return {"name": name, "leds_max": leds_max, "leds_count": leds_count}


def parse_device(data):
    reader = Reader(data)
    reader.u32()
    reader.i32()
    name = reader.text()
    for _ in range(5):
        reader.text()
    mode_count = reader.u16()
    reader.i32()
    modes = [parse_mode(reader) for _ in range(mode_count)]
    zones = [parse_zone(reader) for _ in range(reader.u16())]
    return {"name": name, "zones": zones, "modes": modes}


def controller_ids(sock):
    data = request(sock, 0, 0, b"", 0)
    count = struct.unpack_from("<I", data)[0]
    if count == 0:
        return []
    return list(struct.unpack_from("<" + ("I" * count), data, 4))


def load_devices(sock, version):
    devices = []
    for dev_id in controller_ids(sock):
        device = parse_device(request(sock, dev_id, 1, struct.pack("<I", version), 1))
        device["id"] = dev_id
        devices.append(device)
    return devices


def paint(sock, dev_id, led_count, color):
    payload_size = 6 + (4 * led_count)
    payload = struct.pack("<IH", payload_size, led_count) + struct.pack("<" + ("I" * led_count), *([color] * led_count))
    send_packet(sock, dev_id, 1050, payload)


def encode_mode(mode, color):
    name = mode["name"].encode("utf-8") + b"\0"
    colors = [color] if mode["colors_max"] else []
    body = struct.pack("<H", len(name)) + name
    body += struct.pack(
        "<11I",
        mode["flags"],
        mode["speed_min"],
        mode["speed_max"],
        mode["brightness_min"],
        mode["brightness_max"],
        mode["colors_min"],
        mode["colors_max"],
        mode["speed"],
        mode["brightness"],
        mode["direction"],
        MODE_COLORS_MODE_SPECIFIC if colors else mode["color_mode"],
    )
    body += struct.pack("<H", len(colors))
    if colors:
        body += struct.pack("<" + ("I" * len(colors)), *colors)
    return body


def update_mode(sock, dev_id, mode_index, mode, color):
    encoded = encode_mode(mode, color)
    payload = struct.pack("<Ii", 8 + len(encoded), mode_index) + encoded
    send_packet(sock, dev_id, 1101, payload)


def rgb_color(hue):
    red, green, blue = colorsys.hsv_to_rgb(hue, 1.0, 1.0)
    return (int(blue * 255) << 16) | (int(green * 255) << 8) | int(red * 255)


def main():
    sock = socket.create_connection((HOST, PORT), 10)
    version = struct.unpack("<I", request(sock, 0, 40, struct.pack("<I", PROTOCOL), 40))[0]
    send_packet(sock, 0, 50, b"ligth-sync\0")
    drain(sock)

    devices = []
    for _ in range(40):
        devices = load_devices(sock, version)
        if devices:
            break
        time.sleep(0.5)
    if not devices:
        raise RuntimeError("OpenRGB did not detect any devices")

    for device in devices:
        for index, zone in enumerate(device["zones"]):
            if "Addressable" in zone["name"] and zone["leds_count"] != min(STRIP_LEDS, zone["leds_max"]):
                send_packet(sock, device["id"], 1000, struct.pack("<ii", index, min(STRIP_LEDS, zone["leds_max"])))
    drain(sock)
    for _ in range(20):
        devices = load_devices(sock, version)
        headers = [zone for device in devices for zone in device["zones"] if "Addressable" in zone["name"]]
        if headers and all(zone["leds_count"] >= min(STRIP_LEDS, zone["leds_max"]) for zone in headers):
            break
        time.sleep(0.25)

    led_targets = []
    for device in devices:
        led_count = sum(zone["leds_count"] for zone in device["zones"])
        send_packet(sock, device["id"], 1100, b"")
        led_targets.append((device["id"], led_count, device["name"]))
        print(f"{device['name']}: {led_count} LEDs", flush=True)
    drain(sock)

    next_frame = time.perf_counter()
    while True:
        hue = (time.time() % PERIOD_SECONDS) / PERIOD_SECONDS
        color = rgb_color(hue)
        for dev_id, led_count, _ in led_targets:
            if led_count:
                paint(sock, dev_id, led_count, color)
        drain(sock)
        next_frame += FRAME_SECONDS
        delay = next_frame - time.perf_counter()
        if delay > 0:
            time.sleep(delay)
        else:
            next_frame = time.perf_counter()


if __name__ == "__main__":
    main()
