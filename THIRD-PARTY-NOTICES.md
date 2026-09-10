# Third-party components and references

CheckCheck 0.2 uses a self-contained Microsoft .NET 10 Windows Desktop runtime, distributed under its applicable MIT and third-party licenses.

- [.NET license](https://github.com/dotnet/runtime/blob/main/LICENSE.TXT)
- [.NET third-party notices](https://github.com/dotnet/runtime/blob/main/THIRD-PARTY-NOTICES.TXT)
- [WPF license](https://github.com/dotnet/wpf/blob/main/LICENSE.TXT)

Local AI components downloaded on first run (weights are not included inside CheckCheck.exe):

- Qwen3-4B-GGUF, Q4_K_M, by the Qwen team. [Model and Apache 2.0 license](https://huggingface.co/Qwen/Qwen3-4B-GGUF).
- llama.cpp by the ggml-org contributors. [Project and MIT license](https://github.com/ggml-org/llama.cpp). Release b10883 is pinned in ModelCatalog.cs. Runtime archives may include additional component notices.

Bareun is an optional external service operated by Baikal AI. CheckCheck is not affiliated with or endorsed by Bareun. The user's own API key and account plan apply. [Service documentation](https://bareun.ai/docs/howtouse/rest-api/).

Design reference supplied by the user: [Hun-Bot, Smart Korean Grammar Assistant development log](https://hun-bot.dev/ko/blog/devlog/vscode_extension/vscode_extension_01/). The stale-result, protected-text and correction-review discussions informed this integration. The implementation was written for this project; the extension's source code was not copied.

Update flow reference supplied by the user: [Sprache release updater](https://github.com/youkdonghun/Sprache/blob/main/apps/client/lib/src/services/release_update_installer_io.dart). CheckCheck independently implements GitHub version discovery, checksum verification and portable Windows EXE replacement.
