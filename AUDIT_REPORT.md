# Clientry Verification Audit

The audit is evidence-driven. PASS means a command, HTTP call, test, or browser action was run and observed; code inspection alone is not treated as a pass.

## Phase status

| Phase | Status | Current result |
|---|---|---|
| 1. Baseline | **DONE** | Build, tests, install, typecheck, lint, dependency audits, local fresh-stack startup, and repository search completed. Docker is unavailable, so local startup was used. |
| 2. Feature checklist | **PARTIAL** | Core invite/approval/payment journey and some auth/file cases pass. Many individual workflows and state transitions still need explicit verification. |
| 3. Security and isolation | **PARTIAL** | Generated route authorization tests pass; key B-vs-A scenarios pass. Full route-by-route table and remaining security checks are pending. |
| 4. End-to-end scenario | **DONE, WITH LIMITATION** | Playwright completes invite through receipt and checks Client B isolation using a signed local webhook. Stripe PaymentIntent creation could not be verified without a real Stripe secret. |
| 5. UI quality sweep | **PARTIAL** | Three Playwright tests pass, covering the home views, all requested viewport/theme pairs, direct routes, refresh, Back, overflow and browser console errors. Other pages/states and full keyboard/focus review remain. |
| 6. Docs and cleanup | **PARTIAL** | README, `.gitignore`, screenshots, and this report were updated. The final route table, all checklist evidence, and known-limitations review remain. |

## Summary counts (interim)

- **PASS:** 14 checks or scenarios have executed successfully, including all 45 backend tests, three route-generated tests, three browser tests, build/type/lint, and dependency audits.
- **FIXED:** 4 issues found and repaired so far: Swagger schema collision, invalid/binary text accepted as `.txt`, approvals not refetched on entering the inbox, and narrow-screen navigation/demo credential exposure.
- **FAILED:** 0 unresolved failures in the final clean run. Docker and real Stripe are unavailable capabilities, not passing checks.
- These are audit-check counts, not a claim that every Phase 2–6 checklist item is complete; see the tables below.

## Phase 1 — Baseline and repository inspection

| Check | Status | Evidence |
|---|---|---|
| `dotnet build backend/ClientHub.slnx -warnaserror` | PASS | Build succeeded with 0 warnings and 0 errors. |
| `dotnet test backend/ClientHub.slnx` | PASS | 45 passed, 0 failed. |
| `npm ci` | PASS | 120 packages installed; 0 vulnerabilities reported. |
| `npm run build` | PASS | TypeScript project build and Vite production build succeeded. |
| `npx tsc --noEmit` | PASS | Exit code 0. |
| `npm run lint` | PASS | oxlint found 0 issues/warnings on the final run. |
| `npm audit --omit=dev` | PASS | 0 vulnerabilities. |
| `dotnet list backend/ClientHub.slnx package --vulnerable --include-transitive` | PASS | No vulnerable packages reported for the API, Application, Domain, Infrastructure, or Tests projects. |
| Fresh local stack, migration/seed, and HTTP | PASS | Started API against a new SQLite file, Vite client, and Mailpit. Swagger, client root, and Mailpit returned HTTP 200; seeded admin/client login worked. |
| Docker Compose | NOT AVAILABLE | `docker --version` failed because Docker is not installed. Local API/client/Mailpit were used instead. |
| TODO/FIXME, unimplemented exceptions, lorem | PASS | No source hits in backend, frontend source, README, or compose configuration. |
| “not implemented” / placeholder scan | PASS WITH DOCUMENTED HITS | The stale README sentence was replaced by Known limitations. Remaining placeholder hits are intentional form placeholder attributes and their CSS selectors; no stub/placeholder implementation was found. |
| Generic workspace names | PASS WITH SEED DATA | The only Northstar-name hits are the intentional demo-client seed in `backend/ClientHub.Api/Services/DemoData.cs`; no generic workspace fallback uses it. |
| Prefilled demo credentials in the UI | FIXED | Removed the login-screen demo autofill buttons and their now-unused CSS. The UI source no longer contains demo emails or passwords. |
| Browser storage / console | PASS WITH STORAGE NOTES | `localStorage` is used only for the display theme. `sessionStorage` stores the transient access token. No `console.log`, `console.warn`, or `console.error` calls were found in the frontend source. |
| Native controls | REVIEWED | Five semantic `<select>` controls remain (request priority/status/client filters, invoice client, project client) and three native date inputs remain (invoice due date and project start/due dates). They are intentionally retained as keyboard-operable browser controls; custom replacements were not required for this audit. |
| Environment files / secrets | PASS WITH DEVELOPMENT-ONLY DEFAULT | Only `.env.example` is tracked; no `.env`, certificate, or credential file is present. It contains empty secret fields and a replace-before-deploy key. Compose has a development-only fallback signing key and sets `ASPNETCORE_ENVIRONMENT=Development`; `Program.cs` only falls back in Development and requires a configured 32-byte key otherwise. Demo credentials are seed/test data, not production credentials. |
| `.gitignore` coverage | PASS | Covers `bin`, `obj`, `node_modules`, `dist`, uploads, Playwright output, `.env`, and SQLite files. |

## Phase 2 — Feature checklist

Statuses are **PASS**, **FIXED**, or **NOT YET VERIFIED**. Items marked partial in evidence have not been treated as a complete feature pass.

| Area / item | Status | Evidence |
|---|---|---|
| Auth — self-registration disabled by default | NOT YET VERIFIED | Requires direct anonymous registration HTTP check and configuration verification. |
| Auth — admin invite, Mailpit email, browser acceptance, reuse/expiry rejection | PASS | `client-journey.spec.ts` creates an invite, reads its Mailpit link, accepts in Chromium, rejects a second use. `InvitationAcceptanceCreatesClientUserOnceAndRejectsExpiredInvitations` covers expired/used invitation rejection in the 45-test suite. |
| Auth — login | PASS | Fresh seeded admin/client logins succeeded; Playwright signs in both roles. |
| Auth — refresh rotation, old-token reuse rejection, logout revocation | NOT YET VERIFIED | — |
| Auth — forgot/reset, lockout, auth throttling | NOT YET VERIFIED | — |
| Admin — client create/edit/deactivate and deactivated login/data retention | PARTIAL | The browser journey creates a client by the admin API; edit/deactivation behavior remains unverified. |
| Admin — project create/edit/archive/status transitions | PARTIAL | Journey creates a project; other transitions remain unverified. |
| Admin — milestone CRUD/reorder/complete/progress | PARTIAL | Journey creates three milestones and verifies approval completes one; edit/delete/reorder and progress reconciliation remain unverified. |
| Admin — files upload/download/delete and invalid type/size/signature/path names | PARTIAL | Journey uploads a valid text file. `FileUploadsRejectBinaryTextAndMismatchedContentSignatures` passes for binary/invalid UTF-8 text, signature mismatch, and disallowed extension; download/delete/oversize/path traversal remain unverified. |
| Admin — invoice draft/edit/delete/send/void/manual-paid/sequential numbers/totals/PDF | PARTIAL | Journey creates and sends an invoice, receives invoice email, verifies $415 totals and receipt PDF after payment. Draft edit/delete, void/manual payment, sequential numbering and ordinary invoice PDF remain unverified. |
| Admin — dashboard counts against direct DB query | NOT YET VERIFIED | — |
| Admin — workspace settings persisted/used by UI, email, PDF | PARTIAL | Default workspace settings render as Clientry in the browser; save and downstream email/PDF changes remain unverified. |
| Admin — activity/audit log for every mutation | NOT YET VERIFIED | — |
| Client — tenant isolation for all projects/files/comments/invoices/notifications/requests/approvals | PARTIAL | Generated isolation test and end-to-end Client B checks cover representative lists and foreign resource IDs; every route/resource class remains pending. |
| Client — comments over SignalR with two sessions; own edit/delete | NOT YET VERIFIED | — |
| Client — approval state machine, changes note, repeat 409, admin notification | PARTIAL | Journey requests approval, verifies client approval completes milestone; changes-note/repeat-decision/admin-notification assertions remain pending. |
| Client — work requests create/cancel/accept/decline/convert | NOT YET VERIFIED | — |
| Client — needs-attention overview matches database | PARTIAL | The overview loads in the browser and is captured; direct data-to-database comparison remains pending. |
| Client — image/PDF preview, drag/drop progress, zip | NOT YET VERIFIED | — |
| Client — Stripe PaymentIntent, webhook, replay/amount/currency/payment_failed, paid receipt | PARTIAL | No Stripe key is available; PaymentIntent endpoint returns 503 as expected. Signed `payment_intent.succeeded` webhook passes; wrong amount returns 400; replay is idempotent; paid receipt returns a PDF. Currency mismatch and `payment_failed` remain unverified. |
| Client — Owner team invite/deactivate/billing; Member receives 403 | NOT YET VERIFIED | — |
| Client — notifications, unread/live push/preferences/throttling | NOT YET VERIFIED | — |
| Client — profile and server-side preferences | NOT YET VERIFIED | — |

## Phase 3 — Security and isolation

`RouteAuthorizationTests` passes all three generated tests:

- Every protected API route in the API description inventory returns 401 to an anonymous request.
- Every Admin-only route returns 403 to both seeded Client Owner and Client Member tokens.
- Cross-client project/file/zip/invoice/PDF/receipt/request/approval/notification reads or decisions return 404; client list routes do not contain Client B identifiers; `/api/me/team` and `/api/me/billing` omit Client B data.

The Swagger inventory contains **84 method/path operations across 65 paths**. The final report still needs the required per-operation method/route/role/isolation table and coverage for every client-scoped endpoint, SignalR group joins, null-ClientId behavior, and remaining security checks (CORS/security headers, logs, token/password hashes, SQL parameters, and upload path handling).

## Phase 4 — End-to-end scenario

`frontend/e2e/client-journey.spec.ts` passed in Chromium against a fresh local SQLite database, local API, and Mailpit:

1. Admin logs in, creates a client, and invites a user.
2. The invitation email arrives in Mailpit; the user follows its link and accepts in the browser. Reuse fails.
3. Admin creates a project and three milestones, uploads a file, and requests approval; Mailpit receives approval email.
4. Client approves; API confirms the milestone is completed/Approved.
5. Admin creates/sends an invoice; the client sees the invoice email.
6. PaymentIntent creation returns 503 because no Stripe secret is configured. A generated signed success webhook marks the invoice Paid; a wrong amount returns 400, replay does not duplicate payment, and receipt PDF downloads.
7. Client B cannot see the journey's IDs in list endpoints and gets 404 for the project, file, and receipt.

The Playwright screenshot is `docs/screenshots/e2e-client-paid-invoice.png`. The real Stripe gateway/card flow remains unavailable.

## Phase 5 — UI quality sweep

The complete browser test run passed **3/3 tests**:

- Client and admin home views at 360, 768, and 1280 pixels in light and dark themes (12 screenshots).
- No horizontal overflow and no browser console/page errors at the tested home viewports.
- Client direct routes, Back navigation, request-route refresh/session persistence, and approval navigation.
- Admin workspace default setting and route persistence.

The sweep does not yet cover every page's loading/empty/error states, all forms' inline validation, keyboard focus visibility, expired-token redirection, or all deep links.

## Phase 6 — Docs and cleanup

- README known limitations and client guide are present; `.env.example` is tracked and `.gitignore` now excludes generated/test/upload/database output.
- Responsive and end-to-end screenshots are in `docs/screenshots/`.
- The final route matrix, complete verification evidence, unused-code/package review, and known-limitations list remain.

## Exact validation commands and current results

| Command | Result |
|---|---|
| `dotnet build backend/ClientHub.slnx -warnaserror` | PASS, 0 warnings / 0 errors |
| `dotnet test backend/ClientHub.slnx` | PASS, 45 passed / 0 failed |
| `dotnet test backend/ClientHub.slnx --no-build --filter FullyQualifiedName~RouteAuthorizationTests` | PASS, 3 passed |
| `dotnet test backend/ClientHub.slnx --no-build --filter FullyQualifiedName~FileUploadsRejectBinaryText` | PASS, 1 passed |
| `npm ci` | PASS, 120 packages installed |
| `npm run build` | PASS |
| `npx tsc --noEmit` | PASS |
| `npm run lint` | PASS, 0 issues |
| `npm audit --omit=dev` | PASS, 0 vulnerabilities |
| `dotnet list backend/ClientHub.slnx package --vulnerable --include-transitive` | PASS, none reported |
| `npm run test:e2e -- --workers=1` | PASS, 3 browser tests |
| Docker Compose | NOT AVAILABLE, Docker CLI missing |

## Known limitations / not yet verified

- No real Stripe secret key or publishable key is available. The PaymentIntent/card gateway flow is not verified; the signed local webhook path is verified.
- SMTP is verified through Mailpit, not an external provider.
- Docker Compose could not run because Docker is not installed; the API/client/Mailpit stack was started locally.
- Workflows and security properties explicitly labeled NOT YET VERIFIED above must not be interpreted as passing.
