# Local Model Integrator

Bring your own AI model into Visual Studio — local or remote — as a **code-aware, read-only assistant** that stays strictly inside your open solution and is honest about where your code goes.

Point it at any OpenAI-compatible endpoint (Ollama, LM Studio, vLLM, or the OpenAI API). It reads only the currently-open solution, suggests changes in chat for you to apply yourself, and never writes to your files.

## What it does

- **Agentic, code-aware chat.** Every message can run a tool-using loop that inspects your *actual* code with Roslyn (find references, symbols, file outlines), content search, and file reads — so answers come from your code, not a guess.
- **Works with any OpenAI-compatible model.** Uses native function/tool-calling when your endpoint supports it, and falls back to a text-based tool protocol for models that don't — so even a modest local model is useful.
- **Editor actions.** Right-click in a code file for Explain / Refactor / Generate Tests / Add Doc Comments.
- **Streaming responses** with a collapsible reasoning view, prompt history (Ctrl+↑ / Ctrl+↓), and a Test Connection probe.

## Your code, your boundary

This extension is deliberately conservative about your source:

- **Solution-only.** It can read only files in the currently-open solution. No solution open → it's disabled. Files outside the solution are refused, even if open in another tab.
- **One destination, your choice.** Your code is only ever sent to the API URL you configure. Nothing phones home.
- **Visible egress.** A chip shows where requests go and whether they're encrypted (🟢 this machine · 🟡 external + https · 🔴 external + http), and you're asked once before anything leaves your machine.
- **Read-only.** It proposes edits in chat; you review, copy, and apply them. It does not touch your files.

## Setup

1. **Tools → Options → Local Model Integrator** — set your **API URL**, **Model Name**, and **Protocol**.
2. **Test Connection** to confirm reachability and capabilities.
3. Open the chat (**View → Other Windows → Local Model Integrator**), open a solution, and start asking.

## Requirements

- Visual Studio 2022 (17.x or later).
- An OpenAI-compatible chat-completions endpoint — local (Ollama, LM Studio, vLLM, …) or remote.

Source and issues: https://github.com/lxman/LocalModelIntegrator
