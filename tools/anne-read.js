// Lê informações do Anne Pro 2 pelo Bluetooth (só comandos de leitura).
// Uso: node tools/anne-read.js   (aperte uma tecla antes, para o teclado acordar)
const HID = require('node-hid');

const info = HID.devices().find(d => d.vendorId === 0x0d && d.productId === 0xf0e0 && d.usagePage === 0xff00);
if (!info) { console.log('Anne Pro 2 não encontrado no Bluetooth.'); process.exit(1); }

const dev = new HID.HID(info.path);
dev.on('data', b => console.log('<-', Buffer.from(b).toString('hex')));
dev.on('error', e => console.log('erro:', e.message));

// 7B 12 [destino<<4 | 2] 00 LEN 00 00 7D GRUPO SUB
const ask = (dst, grp, sub) => {
  const p = [0x02, 0x7b, 0x12, (dst << 4) | 2, 0x00, 2, 0, 0, 0x7d, grp, sub];
  while (p.length < 19) p.push(0);
  dev.write(p);
};

const cmds = [
  ['firmware', 3, 0x01, 0x03],
  ['tempo ligado (h)', 3, 0x01, 0x02],
  ['estado', 3, 0x01, 0x09],
  ['MAC Bluetooth', 5, 0x40, 0x30],
];

(async () => {
  for (const [name, dst, grp, sub] of cmds) {
    console.log('->', name);
    ask(dst, grp, sub);
    await new Promise(r => setTimeout(r, 800));
  }
  dev.close();
})();
