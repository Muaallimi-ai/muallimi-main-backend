# Phase 3 — StudentExperience Contract Tests

Contract tests for the seven Phase 3 contracts:

- `student-experience-contract.md` — home dashboard + session lifecycle
- `lesson-viewer-retrieval-contract.md` — Study mode retrieval facade
- `student-tutor-chat-contract.md` — text + voice tutor exposure
- `quiz-and-mock-test-contract.md` — Solve Questions + Mock Test
- `homework-help-image-contract.md` — Homework Help (text, voice, image)
- `whiteboard-session-contract.md` — plan-gated whiteboard
- `session-event-contract.md` — Phase 4-facing session events

Subfolders follow the contract's owning surface. Test classes load the
corresponding contract stub from
`src/Muallimi.Api/StudentExperience/Contracts/`.
