# Capabilities Overview Page — Design

**Date:** 2026-06-03
**Status:** Approved (design)
**Scope:** Sub-project 1 of 2. (Sub-project 2 = WhatsApp "typing…" presence indicator, brainstormed separately after this ships.)

## Goal

Add a read-only "What Erda can do" page to the Vue control-panel SPA: a curated, plain-language overview of Erda's capabilities (vault tools, reasoning, reminders, voice memos, error watch, WhatsApp), with small technical tags. KISS — short copy, no editable config, no backend.

## Decisions

- **Content source:** static, curated. Hand-written copy in the component; no `/api/capabilities` endpoint. Single-user project, acceptable drift risk.
- **Detail level:** friendly one-liner per capability + small muted tag chips (model / cadence / scope). No tool function names, no config defaults as body text.
- **Layout:** two-column rows (not cards). Icon + title on the left, description + trailing tags on the right, hairline separators between rows.
- **Grouping:** two sections — "Ask it to do" (on-request tools/workflows) and "Runs on its own" (background automation).

## Page

- **Component:** `web/src/views/CapabilitiesView.vue`, `<script setup lang="ts">` + `<template>` + minimal `<style scoped>`.
- **Route:** `/capabilities`, registered in `web/src/router.ts` (import + entry in `routes`). Covered by the existing `router.beforeEach` auth guard — no auth changes.
- **Nav:** new `<RouterLink to="/capabilities">` in `web/src/components/Sidebar.vue`, added **last** in the `Operate` nav, icon `zap`, label `Capabilities`. (`active-class="active"`, matching siblings.)
- **Structure:** `.page` > `.page-header` (h1 "What Erda can do" + one-line subtitle) > two `<h2>` group headings, each followed by a row list.

## Data shape

A typed static array in the component; the template maps over it. No separate data module.

```ts
interface Capability {
  group: 'ask' | 'auto'
  icon: string   // Icon.vue name
  title: string
  desc: string
  tags: string[]
}
```

## Content (6 rows)

**Ask it to do** (`group: 'ask'`)

| icon | title | desc (illustrative; tighten in code) | tags |
|---|---|---|---|
| `note` | Vault notes | Reads, searches and writes notes in your Obsidian vault | `read · write · search`, `vault-confined` |
| `globe` | Deep reasoning | Hands hard questions to a stronger model with live web search | `gpt-5.5`, `web search` |
| `clock` | Reminders & prompts | Schedules one-off or recurring reminders, and prompts that run live | `cron / one-off`, `Europe/Berlin` |
| `mic` | Voice memos | Turns an Apple Voice Memo into a clean, structured note | `gpt-4o-transcribe → gpt-5.5` |

**Runs on its own** (`group: 'auto'`)

| icon | title | desc (illustrative; tighten in code) | tags |
|---|---|---|---|
| `alert` | Error watch | Watches Seq for errors and pings you on WhatsApp with a diagnosis | `Seq`, `every 15 min` |
| `chat` | WhatsApp | Talk to Erda by text, voice or image — and it messages you proactively | `text · voice · image` |

All icon names verified to exist in `web/src/components/Icon.vue` (`note`, `globe`, `clock`, `mic`, `alert`, `chat`, `zap`).

## Styling

- Reuse existing tokens/utilities: `.page`, `.page-header`, `var(--*)` tokens, `.badge` (+ color variants) for tags.
- `<style scoped>` only for: the two-column row grid (e.g. `grid-template-columns: minmax(0, 220px) 1fr` or label/value split), row separators (`border-bottom: 1px solid var(--border)`), and the left icon+title cell alignment. Keep it minimal and theme-token-driven (works in dark + light).

## Out of scope

- No backend / API / DTO changes.
- No live config or on/off state.
- No editing.
- WhatsApp typing-indicator row — added when sub-project 2 ships.

## Testing / verification

The SPA has no unit-test harness (the repo's tests are .NET/xUnit). Verification:

1. `cd web && npm run build` — `vue-tsc` type-check + `vite build` pass (no type errors, no unused-symbol errors).
2. Visual check via `make web` (Vite at :5173): page renders under `/capabilities`, nav link appears and activates, both groups + all 6 rows show, tags render, layout holds in dark and light themes and at narrow width.
