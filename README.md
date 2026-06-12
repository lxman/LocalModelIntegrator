# Local Model Integrator

[![Visual Studio Marketplace](https://img.shields.io/badge/Visual%20Studio%20Marketplace-install-5C2D91)](https://marketplace.visualstudio.com/items?itemName=michaeljordanlxman.LocalModelIntegrator)

A Visual Studio 2026 extension that turns any OpenAI-compatible model — local or remote — into a **code-aware, read-only assistant** scoped strictly to your open solution.

Point it at your own endpoint (Ollama, LM Studio, vLLM, or the OpenAI API). It reads only the currently-open solution, shows you exactly where your code is being sent, and suggests changes in chat for you to apply by hand. It never writes to your files.

## Why

Most in-IDE AI assistants assume a specific cloud provider and send your code there. This one assumes the least: any model behind a URL, running wherever you want — including entirely on your machine. It's for people who want their own model, their own data boundary, and a tool that's honest about both.

## Data boundary (read this)

- **Solution-only.** The model can read only files that belong to the currently-open solution. With no solution open, it's disabled. Files outside the solution — even ones open in another editor tab — are refused.
- **You choose the endpoint.** The only place your code is sent is the API URL you configure. The extension does not phone home.
- **You can see where it goes.** A chip under the input shows the destination and whether the connection is encrypted (🟢 this machine · 🟡 external + https · 🔴 external + http), and asks once before anything first leaves your machine.
- **Read-only.** It suggests edits in chat; you select, copy, and apply them yourself. It does not modify your files.

## Features

- **Agentic, code-aware chat.** By default every message runs a tool-using loop that inspects your actual code (Roslyn find-references / symbols / outlines, content search, file read) instead of guessing.
- **Any OpenAI-compatible model.** Native `tool_calls` when the endpoint supports them; a text-based tool protocol fallback for models that don't — so even a modest local model works.
- **Streaming responses** with a collapsible "thinking" view.
- **Editor right-click actions:** Explain, Refactor, Generate Tests, Add Doc Comments (limited to in-solution files).
- **Prompt history** in the input box (Ctrl+↑ / Ctrl+↓).
- **Capability probe / Test Connection** so you know what your endpoint supports.
- **Context-budget management** to keep long sessions inside your model's window.

## Requirements

- Visual Studio 2026 (version 18).
- An OpenAI-compatible chat-completions endpoint (e.g. Ollama, LM Studio, vLLM, or `api.openai.com`).

## Install

From the Visual Studio Marketplace (search "Local Model Integrator"), or download the `.vsix` from the [Releases](https://github.com/lxman/LocalModelIntegrator/releases) page and double-click it.

## Configure

1. **Tools → Options → Local Model Integrator.**
2. Set **API URL**, **Model Name**, and **Protocol** (OpenAI Compatible / Ollama / DeepSeek vLLM / LM Studio).
3. Click **Test Connection** to confirm reachability and capabilities.
4. Open the chat from **View → Other Windows → Local Model Integrator**, open a solution, and ask away.

Useful options: **Context Window (tokens)**, **Agent Max Steps** (0 = unlimited), **Request Timeout (ms)** (0 = no timeout), **Agentic Chat** (toggle tool use), **Enable Reasoning / Streaming**.

## Build from source

Requires VS 2022 with the **Visual Studio extension development** workload.

```powershell
msbuild src\LocalModelIntegrator\LocalModelIntegrator.csproj /t:Restore
msbuild src\LocalModelIntegrator\LocalModelIntegrator.csproj /p:Configuration=Release
```

The VSIX lands in `src\LocalModelIntegrator\bin\Release\net472\`.

## License

[MIT](LICENSE)
