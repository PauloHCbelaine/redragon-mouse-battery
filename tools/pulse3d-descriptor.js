// Mostra o descritor HID (somente leitura) de cada coleção do dongle do PULSE 3D.
const HID = require('node-hid');
for (const d of HID.devices().filter(x => x.vendorId === 0x054c && x.productId === 0x0d5e)) {
  console.log(`\niface ${d.interface} page ${d.usagePage.toString(16)} usage ${d.usage.toString(16)} release ${d.release.toString(16)}`);
  try {
    const h = new HID.HID(d.path);
    const desc = h.getReportDescriptor();
    console.log(Buffer.from(desc).toString('hex').match(/../g).join(' '));
    h.close();
  } catch (e) { console.log('erro:', e.message); }
}
