// Teste de gravação seguro: lê o polling atual e grava O MESMO valor de volta (não muda nada),
// mostrando a resposta do mouse e a leitura depois.
const HID = require('node-hid');
const d = HID.devices().filter(x => x.vendorId === 0x3554 && x.usagePage === 0xff02)
  .sort((a, b) => (b.productId === 0xf5d5) - (a.productId === 0xf5d5))[0];
const h = new HID.HID(d.path);
const hex = r => Buffer.from(r).toString('hex').match(/../g).join(' ');

function frame(cmd, addr, data) {
  const b = Buffer.alloc(17);
  b[0] = 0x08; b[1] = cmd; b[3] = addr >> 8; b[4] = addr & 0xff; b[5] = data.length;
  data.forEach((v, i) => b[6 + i] = v);
  b[16] = (0x55 - b.subarray(0, 16).reduce((a, x) => a + x, 0)) & 0xff;
  return b;
}
function send(cmd, addr, data) {
  while (h.readTimeout(5).length) {}
  const f = frame(cmd, addr, data);
  console.log('envia  ', hex(f));
  h.write([...f]);
  for (let i = 0; i < 15; i++) {
    const r = h.readTimeout(60);
    if (r.length && r[1] === cmd && ((r[3] << 8) | r[4]) === addr) { console.log('resposta', hex(r)); return r; }
  }
  console.log('resposta: nenhuma');
  return null;
}

const before = send(0x08, 0x0000, new Array(2).fill(0));
if (!before || before[2] !== 0) { console.log('Leitura falhou, nada gravado.'); process.exit(1); }
const v = process.argv[2] ? parseInt(process.argv[2], 16) : before[6];
console.log(`polling atual: código ${before[6]}. Gravando código ${v}…`);
send(0x07, 0x0000, [v, (0x55 - v) & 0xff]);
const after = send(0x08, 0x0000, new Array(2).fill(0));
console.log(after && after[6] === v ? 'OK: valor igual depois da gravação.' : 'ATENÇÃO: valor diferente!');
h.close();
