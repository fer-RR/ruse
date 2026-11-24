# Ruse — Envie áudio para qualquer lugar no Windows

[English](README.md) · **Português (Brasil)**

Um roteador de áudio gratuito e de código aberto com uma tela visual. Capture o
som de aplicativos, do PC inteiro ou de um microfone, modele com efeitos e envie
para caixas de som, fones, cabos virtuais, outro computador ou um gravador — tudo
em um só lugar.

[Baixar para Windows](https://github.com/fer-RR/ruse/releases/latest) · [Recursos](#recursos) · [Primeiros passos](#primeiros-passos) · [Configurações comuns](#configurações-comuns) · [Compilar do código](#compilar-do-código)

[![Última versão](https://img.shields.io/github/v/release/fer-RR/ruse?label=última&sort=semver)](https://github.com/fer-RR/ruse/releases/latest)
[![Licença: MIT](https://img.shields.io/badge/licen%C3%A7a-MIT-green.svg)](LICENSE)
[![Plataforma: Windows 10 e 11](https://img.shields.io/badge/plataforma-Windows%2010%20%7C%2011-blue.svg)](#requisitos)

Ruse é um roteador de áudio para Windows 10 e 11. Monte um grafo de roteamento
arrastando fontes, efeitos e destinos para uma tela e ligando tudo com cabos. Ele
**não** é uma DAW, **não** instala driver de kernel e o ASIO está indisponível
nesta versão. Ele usa os dispositivos que você já tem, incluindo cabos virtuais
como VB-CABLE e Voicemeeter.

Tudo roda no seu PC. O Ruse nunca envia seu áudio para a internet. Os modelos
opcionais de transcrição, tradução e voz também rodam localmente depois do
download.

## O que você pode fazer com o Ruse

- **Ouvir um aplicativo em dois lugares.** Ouça o jogo no fone enquanto envia uma
  cópia para um cabo virtual para stream ou gravação.
- **Separar voz e áudio do jogo.** Envie Discord, jogo, microfone e música para
  dispositivos diferentes, com volumes independentes.
- **Melhorar o microfone.** Adicione equalização, compressão, redução de ruído,
  de-esser, gate e limitador antes de o mic chegar ao Discord, OBS ou uma
  chamada.
- **Montar uma mixagem de monitoração.** Use o mixer, envios, ducking por
  sidechain e processamento por rota para uma pequena apresentação ou stream.
- **Ouvir melhor.** Use o perfil audiológico para equalização e ganho por ouvido,
  atraso de sincronia labial, nivelamento de volume e um limitador de segurança.
- **Gravar a mixagem final.** Salve WAV, FLAC ou MP3 de qualquer rota ou
  renderize a linha do tempo offline.
- **Enviar áudio para outro PC.** Use um link Ruse para Ruse na sua rede local.
- **Usar seus plugins favoritos.** Hospede efeitos e instrumentos VST3 com
  segurança.
- **Ver o que está acontecendo.** Acompanhe espectro, espectrograma, forma de
  onda, nível, volume e medidores de pico real.
- **Automatizar.** Controle o grafo por MIDI, atalhos de teclado ou pela API de
  controle local opcional.

## Recursos

### Roteamento

- Captura por aplicativo, áudio do sistema, microfones e entradas de linha.
- Envio para caixas de som, fones e cabos virtuais existentes.
- Arquivos, tons de teste, metrônomo, gerador de som e entrada de rede como
  fontes.
- Rotas diretas de fonte para dispositivo e passthrough nativo para áudio sem
  alteração.
- Adicione efeitos entre dois nós ou solte direto sobre um cabo.
- Solte um nó em cima de outro e o Ruse conecta os dois para você.

### Efeitos e plugins

- **63 efeitos embutidos**, incluindo equalização e filtros, dinâmica, gates,
  saturação, afinação e auto-tune, vocoder, delays, reverbs, convolução e efeitos
  de caráter.
- Efeitos **e instrumentos** VST3, hospedados em um processo separado para que um
  plugin travado não feche o Ruse.
- Salve e carregue presets de plugins e abra editores de plugin sobre o app.
- Cadeias de efeitos, cadeias salvas e modelos de quadro reutilizáveis.
- Grupos de mixer com ganho, mute e solo próprios.

### Mixer e linha do tempo

- Mixer completo com nível, pan, polaridade, atraso, crossfade A/B, largura,
  ganho automático e medidores por barramento.
- Envios e sidechain com nível, polaridade e mute da chave.
- Linha do tempo com faixas, clipes, fades, ganho, afinação, alongamento,
  automação, marcadores e regiões.
- Edição com seleção múltipla, normalizar, inverter, consolidar e bounce ou
  congelamento offline.

### Voz, quando quiser

- Transcrição e tradução locais com Whisper.
- Transcrições parciais ao vivo com Vosk, junto com o Whisper.
- Texto para voz com Kokoro ou Piper, com interrupção e abaixamento de volume.
- Use texto recebido pela rede como fonte de tradução ou de voz.

### Saída e gravação

- Gravação em WAV (16/24 bits ou 32 bits float), FLAC e MP3.
- Bounce e congelamento da linha do tempo com todo o grafo de roteamento.
- Áudio Ruse para Ruse por UDP em uma rede local confiável.
- Pacotes verificados e atualizador opcional dentro do app.

### Extras

- Cinco temas: Dark, Light, Blueprint, Studio '74 e Vesper.
- Atalhos de teclado, opção de movimento que pode sobrepor a preferência do
  Windows e rótulos para leitores de tela.
- Interface em inglês, alemão, espanhol, francês, italiano, japonês, coreano,
  português (Brasil) e chinês simplificado.
- API de controle local opcional para scripts e automação.

## Baixar e instalar

1. Abra a [última versão](https://github.com/fer-RR/ruse/releases/latest).
2. Baixe `Ruse-<versão>-win-x64.zip`.
3. Extraia o ZIP e execute `Ruse.exe`.
4. Opcionalmente, confira o download com o arquivo `.sha256` publicado.

O pacote é autossuficiente e inclui o runtime do .NET. Ele não inclui plugins
VST3 nem arquivos de modelos de voz ou tradução.

## Requisitos

- **Sistema:** Windows 10 ou 11, 64 bits.
- **Captura por aplicativo:** Windows 11 ou Windows Server 2022.
- **Runtime:** Microsoft Edge WebView2. O Windows 11 já inclui; a maioria dos
  Windows 10 também tem, e o Ruse leva ao instalador se estiver faltando.
- **Compilar do código:** .NET 10 SDK e Node.js 20 ou mais novo.
- **Modelos e drivers:** Whisper, tradução, voz e plugins VST3 são instalados
  separadamente.
- **Link de rede:** mesma rede local confiável, com áudio UDP e texto TCP sem
  criptografia.

## Primeiros passos

1. **Adicione uma fonte.** Arraste um aplicativo, áudio do sistema, microfone,
   arquivo ou tom da biblioteca para a tela.
2. **Adicione um destino.** Arraste caixas de som, fones, um cabo virtual, um
   gravador ou um link de rede para a tela.
3. **Conecte.** Arraste da porta **OUT** da fonte para a porta **IN** do destino —
   ou simplesmente solte um nó em cima do outro.
4. **Adicione processamento (opcional).** Solte um efeito sobre um cabo ou entre
   dois nós.
5. **Inicie o roteamento.** Pressione o botão de energia na barra de ferramentas.
   Abra o **Mixer** para volumes e a **Linha do tempo** para clipes e automação.

O tour inicial e os botões de ajuda `?` explicam os principais fluxos.

## Configurações comuns

| Objetivo | Rota sugerida |
| --- | --- |
| Ouvir um jogo e enviar ao Discord | Jogo → Equalização → Fones e VB-CABLE |
| Usar o microfone no Discord e no OBS | Microfone → Passa-alta → Compressor → Limitador → VB-CABLE |
| Gravar a mixagem de uma stream | Apps + microfone → Mixer → Gravador de arquivo |
| Enviar áudio e texto para outro PC | Fonte → Link de rede aqui; Link de rede → fones lá |
| Manter a música fora de uma chamada | Música → só fones; microfone → saída da chamada |
| Adicionar um efeito VST3 | Fonte → nó VST3 → destino |
| Ouvir melhor TV ou jogos | App ou áudio do sistema → Perfil audiológico → Fones |

## Seus arquivos

O Ruse mantém seu conteúdo em `Documents\Ruse`:

- `Presets` — presets de roteamento salvos.
- `Recordings` — gravações do gravador e da linha do tempo.
- `Samples` — amostras importadas e mídias.
- `Exports` — bounces, clipes consolidados e transcrições exportadas.
- `Notes` — notas de texto salvas.
- `Support` — pacotes de suporte exportados.

Configurações, sessões, cadeias, modelos de quadro, logs e modelos baixados ficam
em `%APPDATA%\Ruse` e `%LOCALAPPDATA%\Ruse`. Configurações > Informações mostra
esses locais e abre sua pasta de usuário. As pastas antigas `Music\Ruse Samples`
e `Music\Ruse Recordings` ainda são lidas para os presets antigos continuarem
funcionando.

## Perfil audiológico e audiogramas

O nó **Perfil audiológico** importa um audiograma real em CSV ou JSON. Os valores
são tratados como limiares de condução aérea em **dB HL**. O Ruse ajusta os
valores para suas bandas de audição, mostra PTA4/severidade e os ganhos
aplicados, e cria um perfil inicial conservador de meio ganho. Ajuste de ouvido
depois — não é uma adaptação clínica.

```csv
frequency,left,right
250,30,20
500,40,30
1000,45,35
2000,50,40
4000,55,45
8000,60,50
```

Também há suporte a audiogramas em JSON do OpenHear.

## Compilar do código

```powershell
dotnet build src\Ruse.App\Ruse.App.csproj
```

Saída: `src\Ruse.App\bin\Debug\net10.0-windows\Ruse.exe`

A compilação também gera a interface React em `src\Ruse.App\WebUi`.

## Testar e empacotar

```powershell
powershell -ExecutionPolicy Bypass -File scripts\regress-ruse.ps1 -Build
powershell -ExecutionPolicy Bypass -File scripts\package-ruse.ps1
```

Testes individuais: `Ruse.exe --effects-test`, `--dsp-test`, `--web-test`,
`--chat-test`, `--selftest`.

Auditoria de licenças de terceiros:

```powershell
powershell -ExecutionPolicy Bypass -File scripts\audit-licenses.ps1
```

## Documentação

- [Backlog](docs/BACKLOG.md)
- [API de controle](docs/CONTROL-API.md)
- [Notas da versão](docs/RELEASE-0.9.22.md)
- [Avisos de terceiros](THIRD-PARTY-NOTICES.md)

## Contribuindo

Issues e pull requests são bem-vindos. Antes de abrir um PR, rode o script de
regressão acima e informe o dispositivo de áudio, o driver e a versão do Windows
usados. Resultados em dispositivos reais são especialmente úteis para
roteamento, VST3 e latência baixa.

## Licença

MIT. Veja [LICENSE](LICENSE) e [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
