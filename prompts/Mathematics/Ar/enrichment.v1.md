---
subject: Mathematics
language: Ar
phase: enrichment
version: 1
author: eslamarafa
notes: |
  Stage 7 — per-node enrichment prompt for embedding.
  Called on approval of a teachable node. Given the node's title, node_type,
  and full body markdown, produce:
    - summary: 2–3 concise Arabic sentences that a Grade 7 student searching
      by concept would recognize. Must reference the specific mathematical
      objects/operations, not just the topic name.
    - concept_keywords: 4–8 canonical Arabic math terms (short noun phrases)
      that this node teaches or references. Prefer terms a student would
      type in an AI-tutor query. Include Western-digit numeric forms only
      when they are part of the concept name (e.g. "الأعداد النسبية").
  Output MUST be JSON exactly matching the schema below. No prose, no markdown
  fences.
  Runtime substitution: {{title}}, {{node_type}}, {{body}} filled by caller.
---
You are enriching one node of an Arabic-language K-12 mathematics textbook so it can be embedded for semantic search by the AI tutor.

Node title: « {{title}} »
Node type: {{node_type}}

Node body (markdown, may contain LaTeX inside `$…$` / `$$…$$`):

```
{{body}}
```

## Task

Produce a summary and a set of concept keywords that describe what a Grade-7 student would learn or look up from this node.

### `summary`

- 2–3 sentences, in fluent Arabic.
- Concrete and specific: mention the actual mathematical objects (e.g. "الجذر التربيعى", "المعادلة من الدرجة الأولى", "الأعداد النسبية") and the operation or property being demonstrated — not just the section title.
- No filler ("هذا الدرس يتناول…") — write it as if it were the first paragraph a student sees when the search result opens.
- Do NOT copy the body verbatim. Distill.
- Preserve LaTeX in `$…$` when a math expression is essential to the meaning.
- Never invent facts not present in the body.

### `concept_keywords`

- 4–8 short Arabic noun phrases.
- Each keyword is a canonical mathematical concept as taught in the Egyptian curriculum — the terms a student would search by.
- Include both broad concepts (e.g. "الجذر التربيعى") and narrower ones present in this node (e.g. "خصائص الجذور", "تبسيط الجذور") when both apply.
- Prefer standard Modern Standard Arabic mathematical vocabulary; do not translate English terms unless the curriculum itself uses them.
- Deduplicate. Do not repeat the same concept in singular and plural.
- Do not include page numbers, chapter names, or the raw title.

## Output format

Return one JSON object, nothing else. No markdown fences, no explanatory text.

```json
{
  "summary": "…",
  "concept_keywords": ["…", "…", "…", "…"]
}
```

If the body is empty, too short to enrich, or clearly not teachable content (e.g. a placeholder), return:

```json
{
  "summary": "",
  "concept_keywords": []
}
```
