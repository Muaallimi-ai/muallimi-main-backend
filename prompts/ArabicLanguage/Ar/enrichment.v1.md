---
subject: ArabicLanguage
language: Ar
phase: enrichment
version: 1
author: eslamarafa
notes: |
  Stage 7 — per-node enrichment prompt for the ArabicLanguage subject.
  Twin of Mathematics/Ar/enrichment.v1.md, adapted for language content
  (grammar rules, vocabulary, literature, comprehension). No LaTeX.
  Runtime substitution: {{title}}, {{node_type}}, {{body}}.
---
You are enriching one node of an Arabic-language K-12 Arabic-language textbook so it can be embedded for semantic search by the AI tutor.

Node title: « {{title}} »
Node type: {{node_type}}

Node body (markdown):

```
{{body}}
```

## Task

Produce a summary and a set of concept keywords that describe what a Grade-7 student would learn or look up from this node.

### `summary`

- 2–3 sentences, in fluent Modern Standard Arabic.
- Concrete: name the actual grammatical rule, literary device, text genre, or vocabulary theme covered — not just the section title.
- If the node teaches a rule, state the rule briefly. If it presents a text (poem, story, passage), name the genre and its central topic. If it covers vocabulary, name the semantic field.
- No filler. Write it as the first paragraph a student sees when the search result opens.
- Do NOT copy the body verbatim. Distill.
- Never invent facts not present in the body.

### `concept_keywords`

- 4–8 short Arabic noun phrases.
- Canonical terms as taught in the Egyptian Arabic-language curriculum — the terms a student would search by.
- Include grammatical categories (e.g. "الفعل المضارع", "المفعول به"), rhetorical devices (e.g. "الاستعارة", "الطباق"), genre labels (e.g. "قصة قصيرة", "قصيدة عمودية"), or vocabulary themes as appropriate to the node.
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

If the body is empty, too short to enrich, or clearly not teachable content, return:

```json
{
  "summary": "",
  "concept_keywords": []
}
```
