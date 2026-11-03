// Lê (somente leitura) a memória de configuração 0x0000-0x00FF do mouse e imprime em hex.
const HID = require('node-hid');
const d = HID.devices().filter(x => x.vendorId === 0x3554 && x.usagePage === 0xff02)
  .sort((a, b) => (b.productId === 0xf5d5) - (a.productId === 0xf5d5))[0];
const h = new HID.HID(d.path);
const hex = r => Buffer.from(r).toString('hex').match(/../g).join(' ');

function frame(cmd, addr = 0, len = 0) {
  const b = Buffer.alloc(17);
  b[0] = 0x08; b[1] = cmd; b[3] = addr >> 8; b[4] = addr & 0xff; b[5] = len;
  b[16] = (0x55 - b.subarray(0, 16).reduce((a, x) => a + x, 0)) & 0xff;
  return b;
}
function q(cmd, addr = 0, len = 0) {
  for (let tries = 0; tries < 5; tries++) {
    while (h.readTimeout(10).length) {}
    h.write([...frame(cmd, addr, len)]);
    for (let i = 0; i < 10; i++) {
      const r = h.readTimeout(60);
      if (r.length && r[1] === cmd && (cmd !== 0x08 || ((r[3] << 8) | r[4]) === addr)) return r;
    }
  }
  return null;
}

for (const c of [0x01, 0x03, 0x04, 0x0a, 0x15, 0x17]) {
  const r = q(c); console.log('cmd', c.toString(16).padStart(2, '0'), r ? hex(r) : '-');
}
for (let a = 0; a < 0x100; a += 10) {
  const r = q(0x08, a, 10);
  console.log(a.toString(16).padStart(4, '0'), r ? hex(r.slice(6, 16)) : '-');
}
h.close();
