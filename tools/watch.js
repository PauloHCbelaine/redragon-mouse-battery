// Observa (somente leitura) a memória de configuração do mouse e registra cada byte que muda,
// com horário. Use enquanto muda configurações no driver oficial da Redragon.
// Resultado também em tools/watch-log.txt. Para parar: Ctrl+C.
const HID = require('node-hid');
const fs = require('fs');
const path = require('path');

const d = HID.devices().filter(x => x.vendorId === 0x3554 && x.usagePage === 0xff02)
  .sort((a, b) => (b.productId === 0xf5d5) - (a.productId === 0xf5d5))[0];
if (!d) { console.log('Mouse não encontrado.'); process.exit(1); }

function frame(addr) {
  const b = Buffer.alloc(17);
  b[0] = 0x08; b[1] = 0x08; b[3] = addr >> 8; b[4] = addr & 0xff; b[5] = 10;
  b[16] = (0x55 - b.subarray(0, 16).reduce((a, x) => a + x, 0)) & 0xff;
  return b;
}
function query(h, addr) {
  for (let tries = 0; tries < 6; tries++) {
    while (h.readTimeout(5).length) {}
    h.write([...frame(addr)]);
    for (let i = 0; i < 10; i++) {
      const r = h.readTimeout(60);
      if (r.length && r[1] === 0x08 && r[2] === 0 && ((r[3] << 8) | r[4]) === addr) return r.slice(6, 16);
    }
  }
  return null;
}
function snapshot() {
  const h = new HID.HID(d.path);
  const mem = [];
  try {
    for (let a = 0; a < 0xc0; a += 10) {
      const r = query(h, a);
      for (let i = 0; i < 10; i++) mem[a + i] = r ? r[i] : null;
    }
  } finally { h.close(); }
  return mem;
}
const hx = v => v.toString(16).padStart(2, '0');
const log = path.join(__dirname, 'watch-log.txt');

let before = snapshot();
console.log('Observando o mouse. Mude as configurações no driver da Redragon (uma de cada vez).');
fs.appendFileSync(log, `\n=== início ${new Date().toLocaleString()} ===\n`);

setInterval(() => {
  let now;
  try { now = snapshot(); } catch (e) { return; }
  const lines = [];
  for (let a = 0; a < now.length; a++)
    if (now[a] != null && before[a] != null && now[a] !== before[a])
      lines.push(`  0x${a.toString(16).padStart(4, '0')}: ${hx(before[a])} -> ${hx(now[a])}`);
  if (lines.length) {
    const out = `${new Date().toLocaleTimeString()} mudou:\n${lines.join('\n')}\n`;
    process.stdout.write(out);
    fs.appendFileSync(log, out);
  }
  for (let a = 0; a < now.length; a++) if (now[a] != null) before[a] = now[a];
}, 2000);
