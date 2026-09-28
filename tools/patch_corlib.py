# Retarget the beta plugin's mscorlib reference (2.0.5.0, key 7cec85d7bea7798e) to the
# Unity 5.6 runtime's mscorlib (2.0.0.0, key b77a5c561934e089), patching only the AssemblyRef row.
import sys, dnfile
p = sys.argv[1]
b = bytearray(open(p, 'rb').read())
pe = dnfile.dnPE(p)
rows = [r for r in pe.net.mdtables.AssemblyRef if str(r.Name) == 'mscorlib']
assert len(rows) == 1
off = rows[0].struct.get_file_offset()
assert b[off:off+8] == bytes.fromhex('0200000005000000'), b[off:off+8].hex()
b[off:off+8] = bytes.fromhex('0200000000000000')
ot = bytes.fromhex('087CEC85D7BEA7798E'); nt = bytes.fromhex('08B77A5C561934E089')
assert b.count(ot) == 1
b = b.replace(ot, nt)
open(p, 'wb').write(b)
print('mscorlib ref -> 2.0.0.0 at', hex(off))
