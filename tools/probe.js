// Teste: pede a bateria ao mouse (comando de leitura 0x04) e mostra a resposta crua.
const HID = require('node-hid');

const VID = 0x3554;
const devs = HID.devices().filter(d => d.vendorId === VID && d.usagePage === 0xff02);
console.log('Interfaces encontradas:');
devs.forEach(d => console.log(` PID ${d.productId.toString(16)} ${d.product} usage ${d.usage.toString(16)} ${d.path}`));

function frame(cmd, target) {
  const b = Buffer.alloc(17);
  b[0] = 0x08; b[1] = cmd;
  const sum = b.subarray(0, 16).reduce((a, x) => a + x, 0);
  b[16] = (target - sum) & 0xff;
  return b;
}

function sleep(ms) { return new Promise(r => setTimeout(r, ms)); }

(async () => {
  for (const d of devs) {
    for (const target of [0x55, 0x4d]) {
      let h;
      try {
        h = new HID.HID(d.path);
        h.write([...frame(0x04, target)]);
        let got = null;
        for (let i = 0; i < 10 && !got; i++) {
          const r = h.readTimeout(100);
          if (r.length && r[0] === 0x08 && r[1] === 0x04) got = r;
        }
        console.log(`PID ${d.productId.toString(16)} checksum 0x${target.toString(16)}:`,
          got ? Buffer.from(got).toString('hex').match(/../g).join(' ') : 'sem resposta');
      } catch (e) {
        console.log(`PID ${d.productId.toString(16)} erro: ${e.message}`);
      } finally { h && h.close(); }
      await sleep(100);
    }
  }
})();
