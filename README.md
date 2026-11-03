# Redragon Mouse Battery

Monitor de bateria leve para a bandeja do Windows, para mouses sem fio **Redragon** com chip **Compx** (USB VID `3554`).
Testado no **Redragon M916 PRO 1K** (dongle 2.4G e cabo).

*Lightweight Windows tray battery monitor for Redragon wireless mice with a Compx chip (VID 3554). English below.*

![tray](https://img.shields.io/badge/Windows-10%20%7C%2011-blue) ![size](https://img.shields.io/badge/sem%20instala%C3%A7%C3%A3o-.exe%20%C3%BAnico-green)

## O que faz

- Mostra a porcentagem da bateria no próprio ícone da bandeja, com cor:
  verde (normal), laranja (≤ 30%), vermelho (≤ 15%), azul (carregando)
- Carregando: mostra um raio com o último nível real de antes do cabo. Enquanto carrega, a voltagem sobe e a porcentagem que o mouse informa fica inflada, então o app não usa esse número. Quando termina de carregar, aparece "F"
- Avisa com notificação em 20% e em 10%
- Clique no ícone para ver os detalhes: bateria, voltagem, tempo restante estimado, conexão (cabo/dongle), polling rate e nível de DPI ativo
- Intervalo de atualização configurável: 5 s, 10 s, 30 s, 1 min ou 5 min
- Opção "Iniciar com o Windows"
- Funciona junto com o driver oficial da Redragon
- **Só lê informações.** Nunca envia comandos de escrita, então não muda nenhuma configuração do mouse.

## Como usar

1. Baixe o `MouseBattery.exe` na página de [Releases](../../releases)
2. Abra. O ícone aparece ao lado do relógio (pode estar escondido na setinha `^`)
3. Clique com o botão direito no ícone para ver as opções

Não precisa instalar nada: usa só o .NET Framework 4, que já vem no Windows 10/11.

> O Windows SmartScreen pode avisar que o app não é conhecido, porque ele não tem assinatura digital. Clique em "Mais informações" → "Executar assim mesmo", ou compile você mesmo (abaixo).

## Compilar

Não precisa de Visual Studio. Rode:

```bat
build.bat
```

Ele usa o compilador C# que já vem no Windows (`%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe`).

## Outros modelos

Outros mouses Redragon, e de outras marcas com chip Compx (VID `3554`), provavelmente funcionam também. Para descobrir, rode `MouseBattery.exe --test`: ele grava a leitura em `teste.txt`, na mesma pasta. Se funcionar ou não funcionar no seu modelo, abra uma issue com o nome do mouse.

A pasta [`tools/`](tools) tem os scripts Node.js usados para descobrir o protocolo. Para usar: `npm install` e depois `node tools/dump.js`.

## Protocolo

Comandos de 17 bytes no report ID `0x08`, na interface HID 1, coleção 5 (usage page `0xFF02`). O checksum (último byte) faz a soma de todos os bytes dar `0x55`.

| Pedido | Resposta |
|---|---|
| `08 04 00…00 49` (bateria) | byte 6 = %, byte 7 = carregando, bytes 8-9 = mV (big-endian) |
| `08 08 00 00 00 06 …` (lê memória 0x0000) | byte 6 = polling (`01`=1000 Hz…), byte 8 = nº de níveis de DPI, byte 10 = nível ativo (0..) |

Baseado no trabalho de [MouseBatteryIndicator](https://github.com/xIIyassineIIx/MouseBatteryIndicator), [ClickSync](https://github.com/Nuitfanee/ClickSync) e [ZaopinBatteryTray](https://github.com/frudrel/ZaopinBatteryTray).

---

## English

Tray app showing the battery percentage of Compx-based Redragon wireless mice (tested on the M916 PRO 1K). It alerts at 20% and 10%, has a configurable refresh interval (5 s to 5 min), and shows voltage, an estimated time remaining, polling rate and the active DPI level. It only sends **read** commands. Download `MouseBattery.exe` from Releases, or build it with `build.bat` (no SDK needed).

## Licença / License

MIT
