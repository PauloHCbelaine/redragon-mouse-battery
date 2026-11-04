# Anne Pro 2: protocolo do ObinsKit

Anotações tiradas do ObinsKit 1.2.11. Os nomes dos comandos estão no JavaScript do app. Os bytes de cada um saem do módulo nativo `@hexcore/node-hid` (`HID.node`). As leituras foram conferidas num Anne Pro 2 com firmware 2.36 (teclas), 2.33 (LED) e 2.05 (Bluetooth), ligado pelo Bluetooth.

## Interfaces

| Conexão | Onde | Relatório |
|---|---|---|
| Cabo USB | `04D9:A292/A293/8008/8009`, interface 1 | 64 bytes, sem ID de relatório |
| Bluetooth LE | `000D:F0E0`, coleção com usage page `0xFF00` (no Windows, `...&Col02`) | 19 bytes, ID de relatório `0x02` |

## Pacote

```
7B  B1  [destino<<4 | origem]  B3  LEN  00  00  7D  GRUPO  SUB  dados...
```

| Campo | Cabo USB | Bluetooth |
|---|---|---|
| B1 | `10` | `12` |
| origem | `1` | `2` |
| B3 | `(total<<4) \| índice` (`10` para um pacote só) | `índice`, com `0x80` quando ainda vem mais pacote (`00` para um pacote só) |
| dados por pacote | até 53 bytes | até 8 bytes |

`LEN` = 2 + quantidade de dados do pacote. A resposta vem no mesmo formato, com origem e destino trocados (ex.: `7B 12 23 ...`). Respostas grandes chegam em vários pacotes (`80`, `81`, ..., e o último sem `0x80`).

Destinos: `3` = processador das teclas, `4` = processador do LED, `5` = processador do Bluetooth.

Exemplo de leitura (Bluetooth): `02 7B 12 32 00 02 00 00 7D 01 03` lê a versão do firmware.

## Comandos (destino, grupo, sub)

Conferidos com resposta do teclado (Bluetooth):

| Comando | Bytes | Resposta |
|---|---|---|
| readFirmwareVersion | 3, `01`, `03` | 6 bytes: teclas `[1].[0]`, LED `[3].[2]`, BLE `[5].[4]` (ex.: `24 02 21 02 05 02` = 2.36 / 2.33 / 2.05) |
| readPowerOnTime | 3, `01`, `02` | uint32 LE, em **horas** |
| readHardwareState | 3, `01`, `09` | 24 bytes (firmware ≥ 2.11): `[12]` camada, `[13]` Magic Fn, `[14]` gatilho de macro, `[15]` Caps Lock, `[16]` brilho do indicador, `[17]` brilho do LED (×10 %), `[18]` manter LED aceso, `[19]` tap, `[20]` modo do tap, `[21]` tempo do tap, `[22..23]` tempo para dormir (uint16 LE) |
| readUDID | 3, `01`, `0D` | número de série |
| readMacroConfig | 3, `30`, `06` | configuração das macros |
| readBleMac | 5, `40`, `30` | 6 bytes, endereço Bluetooth |

Sem resposta pelo Bluetooth (provavelmente só pelo cabo): readBattery (3, `01`, `05`: uint16 LE, voltagem = valor × 2 × 3,1 / 4095), readBatteryPercentage (3, `01`, `11`, só no Anne Pro 2 "Discovery"), readDeviceType (3, `01`, `01`).

Outros, tirados da tabela do `HID.node` (ainda não testados):

| Comando | Bytes |
|---|---|
| readMcuUUID | 3, `01`, `04` |
| resetPowerOnTime | 3, `01`, `0B` |
| writeBacklightAuto | 3, `01`, `0C` |
| getCombinationMode | 3, `01`, `F1` |
| writeKbdMagicFn | 3, `10`, `03` |
| writeKbdTap | 3, `10`, `05` |
| writeKbdLayout (camada) | 3, `10`, `06` |
| writeKbdCapslock | 3, `10`, `07` |
| writeKbdTapkeyActMode | 3, `10`, `11` |
| writeKbdTapkeyModifykeyActMode | 3, `10`, `12` |
| writeKbdTapkeyTimeout | 3, `10`, `13` |
| writeLightList | 3, `20`, `04` |
| writeLightHoldOn | 3, `20`, `0D` |
| writeIndicatorBrightness | 3, `20`, `14` |
| resetMacro | 3, `30`, `04` |
| readMacroActiveList / readMacroMap | 3, `30`, `07` / `08` |
| deleteBleHost | 5, `40`, `05` |
| readVirtualMouseConfig | 3, `60`, `05` |
| writeVirtualMouseCursorSpeed | 3, `60`, `01` |

Cor fixa (Annemone, já usada pelo app): `7B 10 41 10 07 00 00 7D 20 03 FF 01 R G B`. Ela vai direto para o processador do LED, com destino 4 e origem 1.

Como o ObinsKit converte os valores antes de gravar:
- brilho: `valor / 10`
- tempo para dormir: `s < 50 ? 10 + s : (s - 49) * 60`
