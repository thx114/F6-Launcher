"""Read actual managed user strings without concatenating distinct #US entries."""
import struct


def managed_user_strings(data):
    """Return #US literals for a managed PE, or None for a native/non-PE file."""
    if data[:2] != b'MZ':
        return None
    def u16(offset): return struct.unpack_from('<H', data, offset)[0]
    def u32(offset): return struct.unpack_from('<I', data, offset)[0]
    pe = u32(0x3c)
    if data[pe:pe + 4] != b'PE\0\0':
        raise ValueError('Invalid PE signature')
    optional = pe + 24
    magic = u16(optional)
    directory = optional + (112 if magic == 0x20b else 96)
    if magic not in (0x10b, 0x20b):
        raise ValueError('Unknown PE optional header')
    sections = optional + u16(pe + 20)
    def rva_offset(rva):
        for index in range(u16(pe + 6)):
            section = sections + index * 40
            size, address, raw_size, raw = struct.unpack_from('<IIII', data, section + 8)
            if address <= rva < address + max(size, raw_size):
                return raw + rva - address
        if rva < u32(optional + 60): return rva
        raise ValueError('PE RVA outside sections')
    cli_rva = u32(directory + 14 * 8)
    if not cli_rva: return None
    metadata = rva_offset(u32(rva_offset(cli_rva) + 8))
    if data[metadata:metadata + 4] != b'BSJB':
        raise ValueError('Invalid CLR metadata signature')
    header = (metadata + 16 + u32(metadata + 12) + 3) & ~3
    stream_count = u16(header + 2)
    header += 4
    heap = None
    for _ in range(stream_count):
        offset, size = struct.unpack_from('<II', data, header)
        name_end = data.index(0, header + 8)
        name = data[header + 8:name_end]
        header = (name_end + 4) & ~3
        if name == b'#US': heap = data[metadata + offset:metadata + offset + size]
    if heap is None: return []
    values = []
    cursor = 1
    while cursor < len(heap):
        first = heap[cursor]
        cursor += 1
        if first < 0x80: length = first
        elif first < 0xc0:
            length = ((first & 0x3f) << 8) | heap[cursor]
            cursor += 1
        elif first < 0xe0:
            length = ((first & 0x1f) << 24) | int.from_bytes(heap[cursor:cursor + 3], 'big')
            cursor += 3
        else: raise ValueError('Invalid CLR compressed string length')
        if cursor + length > len(heap): raise ValueError('CLR user string outside heap')
        if length:
            # System.String can contain individual surrogate code units.
            values.append(heap[cursor:cursor + length - 1].decode('utf-16le',errors='surrogatepass'))
        cursor += length
    return values
