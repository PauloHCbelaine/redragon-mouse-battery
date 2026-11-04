# Mouse Battery

Monitor de bateria leve para a bandeja do Windows, para mouses sem fio **Redragon** com chip **Compx** (USB VID `3554`).
Testado no **Redragon M916 PRO 1K** (dongle 2.4G e cabo).

*Lightweight Windows tray battery monitor for Redragon wireless mice with a Compx chip (VID 3554). English below.*

![tray](https://img.shields.io/badge/Windows-10%20%7C%2011-blue) ![size](https://img.shields.io/badge/sem%20instala%C3%A7%C3%A3o-.exe%20%C3%BAnico-green)

## O que faz

- Mostra a porcentagem da bateria no próprio ícone da bandeja, com cor:
  verde (normal), laranja (≤ 30%), vermelho (≤ 15%), azul (carregando)
- Carregando: mostra um raio com o último nível real de antes do cabo. Enquanto carrega, a voltagem sobe e a porcentagem que o mouse informa fica inflada, então o app não usa esse número. Quando termina de carregar, aparece "F"
- Avisa com notificação em 20% e em 10%
- **Dispositivos Bluetooth** (teclados, controles, fones…): mostra a bateria que o próprio Windows já guarda, cada um com seu ícone, que tem a barra em cima. Você escolhe quais aparecem em botão direito → "Dispositivos Bluetooth". Testado com o teclado Anne Pro 2. Com o dispositivo desconectado (ou o teclado no cabo, fora do Bluetooth), o ícone mostra o último valor, em cinza
- **Configurar o mouse** sem o driver da Redragon (botão direito → "Configurar mouse…"): valores de DPI de cada nível (50 a 26000), quantidade de níveis, nível ativo, polling (125 a 1000 Hz), LOD e tempo do Peak Performance. O app grava só o que mudou e lê de volta para conferir
- **Perfis do mouse:** salve combinações (ex.: "Jogo", "Trabalho") e troque com um clique pelo menu
- **Anne Pro 2 pelo Bluetooth:** a bateria vem direto do teclado (o Windows às vezes guarda um valor errado, como "0% carregando"), e o card mostra a voltagem, a versão do firmware e as horas de uso, lidas com o mesmo protocolo do ObinsKit (só leitura; veja [`tools/annepro2-protocol.md`](tools/annepro2-protocol.md))
- **Cor do teclado Anne Pro 2** (pelo cabo USB): cores prontas ou qualquer cor, usando o comando descoberto pelo [Annemone](https://github.com/illixion/Annemone). Isso substitui a iluminação configurada no ObinsKit
- Clique no ícone para abrir o **painel**: um cartão por dispositivo, com porcentagem, barra de bateria, voltagem, tempo restante estimado, conexão (cabo/dongle), polling rate e nível de DPI ativo
- **Estado de cada dispositivo** no painel: **Conectado**, **Em repouso** (o mouse e o teclado dormem sozinhos quando ficam parados e acordam ao mexer ou apertar uma tecla) ou **Desconectado** (dongle fora do PC, ou Bluetooth desligado). Desconectado, o card fica cinza e mostra o último nível de bateria e quando ele foi lido
- **Tempo restante** do mouse, do Anne Pro 2 e dos outros dispositivos Bluetooth. O app mede quanto tempo cada ponto de bateria dura, contando só o tempo em que o dispositivo ficou conectado com o PC ligado (PC desligado ou suspenso não entra na conta). A medição fica guardada, então vale depois de reiniciar o PC, e a da última descarga continua valendo depois de uma recarga, até a nova ter caído uns 5%
- Visual no estilo do Windows 11, que segue o tema claro/escuro e a cor de destaque do sistema (e acompanha quando você troca)
- Aviso quando o mouse termina de carregar
- Abrir o app de novo, com ele já rodando, mostra o painel
- "Verificar atualizações" no menu compara com a última versão publicada aqui
- Intervalo de atualização configurável: 5 s, 10 s, 30 s, 1 min ou 5 min
- Opção "Iniciar com o Windows"
- Funciona junto com o driver oficial da Redragon
- O monitor de bateria **só lê**. O app grava no mouse ou no teclado apenas quando você clica em "Aplicar", escolhe um perfil ou escolhe uma cor.

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

## AJAZZ (experimental)

Mouses AJAZZ da família com chip Telink: **AJ139 V2 PRO**, AJ139 PRO, AJ159, AJ159 MC e AJ179 (inclusive o AJ179 V2 MAX), pelo dongle (`249A:5C2F` / `248A:5C2F`) ou no cabo (`248A:5C2E` / `5D2E` / `5E2E`). Só a bateria é lida. O app manda o mesmo pedido de leitura que o app oficial da AJAZZ e não grava nada no mouse.

Protocolo confirmado no AJ179 V2 MAX por [HaloBattery #246](https://github.com/HeyOkay/HaloBattery/pull/246). Os IDs do AJ139 V2 PRO vêm do arquivo de configuração do driver oficial, documentado em [ajazz-control-center](https://github.com/Aiacos/ajazz-control-center). **Ainda não foi testado num AJ139 V2.** Se não funcionar, use **botão direito → Gerar diagnóstico** e abra uma issue com o arquivo que aparece na Área de Trabalho.

Os AJAZZ com chip Compx (**AJ199**) usam o mesmo protocolo do Redragon e devem funcionar como ele.

## Outros modelos

Outros mouses Redragon, e de outras marcas com chip Compx (VID `3554`), provavelmente funcionam também. Para descobrir, rode `MouseBattery.exe --test`: ele grava a leitura em `teste.txt`, na mesma pasta. Se funcionar ou não funcionar no seu modelo, abra uma issue com o nome do mouse.

A pasta [`tools/`](tools) tem os scripts Node.js usados para descobrir o protocolo. Para usar: `npm install` e depois `node tools/dump.js`.

## Protocolo

Comandos de 17 bytes no report ID `0x08`, na interface HID 1, coleção 5 (usage page `0xFF02`). O checksum (último byte) faz a soma de todos os bytes dar `0x55`.

| Pedido | Resposta |
|---|---|
| `08 04 00…00 49` (bateria) | byte 6 = %, byte 7 = carregando, bytes 8-9 = mV (big-endian) |
| `08 08 00 AA AA LL …` (lê `LL` bytes da memória em `AAAA`) | os dados vêm a partir do byte 6 |
| `08 07 00 AA AA LL dados…` (grava na memória) | o mouse devolve o mesmo pacote quando aceita |

Mapa da memória (confirmado no M916 PRO 1K). Valores simples vão em par com `0x55 − valor`:

| Endereço | Configuração |
|---|---|
| `0x0000` | polling: `08`=125, `04`=250, `02`=500, `01`=1000 Hz |
| `0x0002` / `0x0004` | quantidade de níveis de DPI / nível ativo (0..) |
| `0x000A` | LOD (1 ou 2 mm) |
| `0x000C + 4·n` | DPI do nível n: `[lo, lo, hi, chk]`, DPI = (código + 1) × 50, os 2 bits altos ficam nos bits 2-3 de cada metade do byte `hi` |
| `0x00B7` | tempo do Peak Performance, em unidades de 10 s |

Baseado no trabalho de [MouseBatteryIndicator](https://github.com/xIIyassineIIx/MouseBatteryIndicator), [ClickSync](https://github.com/Nuitfanee/ClickSync) e [ZaopinBatteryTray](https://github.com/frudrel/ZaopinBatteryTray).

---

## English

Tray app showing the battery percentage of Compx-based Redragon wireless mice (tested on the M916 PRO 1K). It alerts at 20% and 10%, has a configurable refresh interval (5 s to 5 min), and shows voltage, an estimated time remaining, polling rate and the active DPI level. Each device shows whether it is connected, asleep or disconnected (with its last known battery level). It only sends **read** commands. Download `MouseBattery.exe` from Releases, or build it with `build.bat` (no SDK needed).

## Licença / License

MIT
