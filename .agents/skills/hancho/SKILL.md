---
name: hancho
description: Run and supervise jobs between a supervisor LLM and worker CLI using Hancho (hancho-cli.exe / Hancho.exe MCP). Use for delegating long-running commands, assigning tasks to worker LLMs, managing job queues with dependencies, timeouts, and retries, branching based on results (behavior trees), dashboard monitoring, and inspecting logs, triage cards, memory, and token usage.
---

# Hancho — Foreman between Supervisor and Worker

Hancho is a Windows-based process supervisor for AI agents. The supervisor LLM declares the plan, and Hancho executes and monitors terminal processes. It manages timeouts, process tree termination, logging, success checks, retries, dependency graphs, and a dashboard.

## Overview in 30 Seconds

```text
User
  │ Talks
  ▼
Supervisor LLM (You)       … Plans tasks, submits jobs, evaluates results
  │ Submits work via hancho-cli / MCP
  ▼
Hancho (Foreman)           … Process launch, timeouts, logs, retries, audit
  │ 1 job = 1 command
  ▼
Worker                     … Execution subprocess
                             (claude / opencode / codex / agy / build / test / scripts)
```

| Role | Entity | Responsibilities |
| :--- | :--- | :--- |
| **Supervisor** | LLM talking to the user | Plans and declares jobs, verifies criteria, decides next actions. Does not run heavy commands directly. |
| **Hancho** | `hancho-cli.exe` / `Hancho.exe` | Owns the processes. Manages timeouts, tree kill, logs, retries, dependencies, dashboard. |
| **Worker** | Subprocess CLI or build script | Executes single commands, produces artifacts and outputs. |

**Key Benefits:**
- Long worker logs do not pollute the supervisor context window.
- Processes persist even if the supervisor session disconnects.
- Verifiable success checks (not reliant on LLM self-reporting).
- Failures are structured into triage cards for actionable diagnosis.

## Core Concepts

- **job**: A single executable command. Requires `id`, `command`, `summary`, and `timeoutSec`.
- **taskqueue**: A queue of jobs with dependency resolution (`dependsOn`).
- **behavior tree (BT)**: Declarative state machine branching based on preceding outcomes.
- **escalate**: Points in a behavior tree requiring supervisor human/LLM intervention.
- **triage**: Structured cards analyzing failure classes, evidence, and remediation suggestions.
- **data-dir**: Storage directory for logs, queues, and state. Must be project-consistent.

## Quick CLI Reference

```bash
# Add jobs to a queue
hancho-cli.exe --data-dir .hancho taskqueue add --file jobs.json

# Run queue to completion
hancho-cli.exe --data-dir .hancho run --exit-on-drain

# Inspect job failure triage
hancho-cli.exe --data-dir .hancho taskqueue why <id>
```
