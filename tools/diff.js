// Descobre onde cada configuração fica na memória do mouse (somente leitura).
// Tira uma "foto" da memória 0x0000-0x00FF, espera você mudar UMA configuração no driver
// oficial da Redragon, tira outra e mostra os bytes que mudaram. Repete até digitar "sair".
const HID = require('node-hid');
const readline = require('readline');
const fs = require('fs');
const path = require('path');

const d = HID.devices().filter(x => x.vendorId === 0x3554 && x.usagePage === 0xff02)
  .sort((a, b) => (b.productId === 0xf5d5) - (a.productId === 0xf5d5))[0];
if (!d) { console.log('Mouse não encontrado.'); process.exit(1); }

function frame(cmd, addr = 0, len = 0) {
  const b = Buffer.alloc(17);
  b[0] = 0x08; b[1] = cmd; b[3] = addr >> 8; b[4] = addr & 0xff; b[5] = len;
  b[16] = (0x55 - b.subarray(0, 16).reduce((a, x) => a + x, 0)) & 0xff;
  return b;
}
function query(h, addr) {
  for (let tries = 0; tries < 6; tries++) {
    while (h.readTimeout(5).length) {}
    h.write([...frame(0x08, addr, 10)]);
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
  for (let a = 0; a < 0x100; a += 10) {
    const r = query(h, a);
    for (let i = 0; i < 10; i++) mem[a + i] = r ? r[i] : null;
  }
  h.close();
  return mem;
}
const hx = v => v == null ? '??' : v.toString(16).padStart(2, '0');

const log = path.join(__dirname, 'diff-log.txt');
const rl = readline.createInterface({ input: process.stdin, output: process.stdout });
let before = snapshot();
console.log('Foto inicial tirada. (resultados também vão para tools/diff-log.txt)\n');

function round() {
  rl.question('Mude UMA configuração no driver da Redragon, aplique, escreva o que mudou (ex.: "DPI 1 de 400 para 800") e aperte Enter. Ou "sair": ', what => {
    if (what.trim().toLowerCase() === 'sair') { rl.close(); return; }
    const after = snapshot();
    const lines = [];
    for (let a = 0; a < 0x100; a++)
      if (before[a] !== after[a]) lines.push(`  0x${a.toString(16).padStart(4, '0')}: ${hx(before[a])} -> ${hx(after[a])}`);
    const out = `\n== ${what} ==\n` + (lines.length ? lines.join('\n') : '  (nada mudou)') + '\n';
    console.log(out);
    fs.appendFileSync(log, out);
    before = after;
    round();
  });
}
round();
