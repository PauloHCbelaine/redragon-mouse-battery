// Teste (somente leitura) do Sony PULSE 3D: tenta pedir o relatório 0xB0 e escuta por 15 s.
const HID = require('node-hid');
const devs = HID.devices().filter(d => d.vendorId === 0x054c && d.productId === 0x0d5e);
devs.forEach(d => console.log(`iface ${d.interface} page ${d.usagePage.toString(16)} usage ${d.usage.toString(16)}`));
const d = devs.find(x => x.usagePage === 0xff01 && x.usage === 0x20) || devs.find(x => x.usagePage === 0xff01);
if (!d) { console.log('Coleção 0xFF01 não encontrada'); process.exit(1); }
const h = new HID.HID(d.path);
const hex = r => Buffer.from(r).toString('hex').match(/../g).join(' ');

for (const [name, fn] of [['getInputReport', () => h.getInputReport(0xb0, 8)], ['getFeatureReport', () => h.getFeatureReport(0xb0, 8)]]) {
  try { console.log(name, hex(fn())); } catch (e) { console.log(name, 'falhou:', e.message); }
}

console.log('Escutando 15 s… (aperte um botão do headset, como volume ou mute)');
const end = Date.now() + 15000;
while (Date.now() < end) {
  const r = h.readTimeout(200);
  if (r.length) console.log(new Date().toISOString().slice(11, 19), hex(r));
}
h.close();
