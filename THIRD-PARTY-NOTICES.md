# Third-party notices

Ruse includes the following third-party software and models. This file is a
summary; keep the complete licence texts with any distributed build.

## MIT

- **OpenAI Whisper** — code and model weights, Copyright (c) 2022 OpenAI
  https://github.com/openai/whisper
- **whisper.cpp / ggml** — Copyright (c) 2023-2024 The ggml authors
  https://github.com/ggml-org/whisper.cpp
- **Whisper.net** — Copyright (c) 2024 sandrohanea
  https://github.com/sandrohanea/whisper.net
- **LLamaSharp / llama.cpp** — Copyright (c) The LLamaSharp and llama.cpp authors
  https://github.com/SciSharp/LLamaSharp, https://github.com/ggml-org/llama.cpp
- **OwnAudioVst / OwnVST3Host** — Copyright (c) ModernMube
  https://github.com/ModernMube/OwnAudioVST3
- **Steinberg VST 3 SDK** — Copyright (c) Steinberg Media Technologies GmbH.
  The VST 3.8+ SDK is MIT licensed; plugins hosted by Ruse remain under their
  own licences. https://github.com/steinbergmedia/vst3sdk
- **Microsoft.ML.OnnxRuntime** — Copyright (c) Microsoft Corporation.
  https://github.com/microsoft/onnxruntime
- **Microsoft.ML.Tokenizers** — Copyright (c) Microsoft Corporation.
  https://github.com/dotnet/machinelearning
- **NAudio / NAudio.Asio** — Copyright (c) Mark Heath
  https://github.com/naudio/NAudio
- **React, React DOM, React Flow** — https://react.dev, https://reactflow.dev
- **Whisper model files** — converted from OpenAI Whisper weights under the
  MIT licence.

Permission is hereby granted, free of charge, to any person obtaining a copy of
this software and associated documentation files (the "Software"), to deal in
the Software without restriction, including without limitation the rights to
use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of
the Software, and to permit persons to whom the Software is furnished to do so,
subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.

## ISC

- **Lucide** — Copyright (c) Lucide Contributors
  https://github.com/lucide-icons/lucide
- **d3 modules** used by React Flow — Copyright (c) Mike Bostock
  https://github.com/d3

Permission to use, copy, modify, and/or distribute this software for any
purpose with or without fee is hereby granted, provided that the above
copyright notice and this permission notice appear in all copies.

THE SOFTWARE IS PROVIDED "AS IS" AND THE AUTHOR DISCLAIMS ALL WARRANTIES WITH
REGARD TO THIS SOFTWARE INCLUDING ALL IMPLIED WARRANTIES OF MERCHANTABILITY AND
FITNESS. IN NO EVENT SHALL THE AUTHOR BE LIABLE FOR ANY SPECIAL, DIRECT,
INDIRECT, OR CONSEQUENTIAL DAMAGES OR ANY DAMAGES WHATSOEVER RESULTING FROM LOSS
OF USE, DATA OR PROFITS, WHETHER IN AN ACTION OF CONTRACT, NEGLIGENCE OR OTHER
TORTIOUS ACTION, ARISING OUT OF OR IN CONNECTION WITH THE USE OR PERFORMANCE OF
THIS SOFTWARE.

## Microsoft

- **Microsoft.Web.WebView2** — Copyright (c) Microsoft Corporation.
  Redistribution and use in source and binary forms, with or without
  modification, are permitted under the Microsoft WebView2 licence. See the
  `LICENSE.txt` and `NOTICE.txt` supplied with the NuGet package.
  https://learn.microsoft.com/microsoft-edge/webview2/

## CC-BY 4.0 and Apache 2.0

- **OPUS-MT models** — Copyright (c) Helsinki-NLP. Downloaded separately when
  the user installs a pair; not bundled. Converted ONNX files are provided by
  the Xenova Hugging Face repositories.
  https://huggingface.co/Helsinki-NLP
  https://creativecommons.org/licenses/by/4.0/

## Apache 2.0

- **Qwen2.5 Instruct GGUF models** — Copyright (c) Alibaba Cloud.
  Downloaded separately when the user installs a translation model; not bundled.
  https://huggingface.co/Qwen
- **TypeScript**, **baseline-browser-mapping** and other Apache-licensed build
  dependencies.
  https://www.apache.org/licenses/LICENSE-2.0

## Build-time only

The web UI build uses Vite, TypeScript, browserslist/caniuse-lite and other
development dependencies. They are covered by their respective MIT, Apache-2.0,
ISC, BSD-3-Clause and CC-BY-4.0 licences. They are not part of the runtime UI
bundle except where noted above.
