// Escuta (somente leitura) todas as coleções de entrada do dongle do PULSE 3D por 2 minutos
// e mostra cada relatório recebido. Use enquanto liga/desliga o headset, aperta botões,
// conecta o carregador etc.
const HID = require('node-hid');
const names = { 0xc: 'mídia', 0xff00: 'COL02', 0xff01: 'status' };
const hex = r => Buffer.from(r).toString('hex').match(/../g).join(' ');
const opened = [];
for (const d of HID.devices().filter(x => x.vendorId === 0x054c && x.productId === 0x0d5e && names[x.usagePage])) {
  try {
    const h = new HID.HID(d.path);
    h.on('data', r => {
      let extra = '';
      if (r[0] === 0xb0) {
        const lvl = r[3] === 0x80 ? 'carregando' : r[3] + '%';
        extra = `  -> bateria ${lvl}, ${r[4] & 0x08 ? 'ligado' : 'desligado'}`;
      }
      console.log(new Date().toLocaleTimeString(), names[d.usagePage].padEnd(6), hex(r) + extra);
    });
    h.on('error', e => console.log(names[d.usagePage], 'erro', e.message));
    opened.push(h);
  } catch (e) { console.log('não abriu', names[d.usagePage], e.message); }
}
console.log(`Escutando ${opened.length} coleções por 2 minutos…`);
console.log('Faça: desligar e ligar o headset, mudar o volume, apertar o mute, ligar o cabo de carga.\n');
setTimeout(() => { opened.forEach(h => h.close()); console.log('Fim.'); process.exit(0); }, 120000);
