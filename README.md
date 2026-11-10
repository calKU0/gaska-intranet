# Gąska Intranet

Internal web portal for the employees of Gąska – one place for the everyday company tasks: the address book, time
recording, IT helpdesk tickets and customer relationship management. It works in the browser on computers and phones
and can be installed as an app (PWA).

> **Proprietary software.** Developed on commission for a single client and intended exclusively for the client's
> internal use. No licence is granted to anyone else – see [LICENSE](LICENSE).

The user interface is in Polish; code, comments and documentation are in English.

## Features

| Module | What it does |
|---|---|
| **Sign-in** | Company domain account (Active Directory) plus a second factor: an authenticator app (TOTP), bootstrapped once with an SMS or e-mail code. Trusted devices, idle and absolute session limits. |
| **Pracownicy** (address book) | Searchable list of employees with phones, extensions, e-mail and department, straight from the directory; tap to call or write. |
| **RCP** (time clock) | Start / end of work and breaks from the browser, recorded in the same system as the card readers; live timers, today's records and a weekly summary. |
| **Helpdesk IT** | New tickets (problems and requests) with categories, locations, observers and attachments; the conversation with IT, replies and accepting solutions. Technicians also manage status, urgency, category and assignment. Integrated with GLPI, always acting as the signed-in user. |
| **Kontrahenci** (CRM) | Customer and supplier cards from the company CRM: company data, contact people, visits and tasks with their notes. Adding and editing contractors, contacts and tasks, duplicate detection, company data from the Polish public registers by tax number, and **reading business cards** from a phone photo (local OCR). |
| **Quick links** | Links to other company systems shown in the menu and on the home page. |
| **Help** | Built-in user manual (also as a PDF) and an "install as an app" guide. |
| **Administration** | Module permissions for users and AD groups, "company network only" modules, authenticator resets, trusted devices and an audit log. |

## Technology

| Area | Technology |
|---|---|
| Application | Blazor Web App (.NET 10, interactive server rendering) |
| UI components | MudBlazor, light and dark theme, responsive layout for phones |
| Identity | Active Directory (`System.DirectoryServices`), RFC 6238 TOTP, ASP.NET Core cookie authentication |
| Application data | SQL Server (own schema, EF Core migrations) |
| Integrations | Firebird (CRM), GLPI High-level API v2 and REST API, SMS gateway, SMTP (MailKit), public company registers, OpenStreetMap Nominatim |
| OCR | Tesseract, running locally on the server – photos never leave it |
| Logging | Serilog |
| Hosting | IIS on Windows Server |
| Tests | xUnit |

## Architecture

```
src/
  Gaska.Intranet.Core            domain model and interfaces – no dependency on AD, databases or UI
  Gaska.Intranet.Infrastructure  directory, databases, integrations (CRM, time clock, helpdesk, SMS, e-mail, OCR)
  Gaska.Intranet.Web             Blazor application: pages, layout, sign-in flow, security middleware
tests/
  Gaska.Intranet.Tests           unit tests and opt-in integration tests
tools/
  build-manual.py                builds the PDF user manual from the same HTML the intranet shows
```

- **Modules** are registered in one catalogue (key, title, route, icon, default access). A page opts in with
  `[ModuleAuthorize("…")]` and automatically appears in the menu, on the home page and in the permissions panel.
- **External systems** are reached through interfaces defined in `Core`, so every integration has a fake or demo
  implementation for development, UI work and screenshots – they never touch company data.
- **Writes to external systems** go through the systems' own procedures and run in a single transaction; integration
  tests exercise the full write path inside a transaction that is always rolled back.

## Security

The application is designed to be reachable from the internet:

- **Two-factor authentication** for every sign-in; SMS / e-mail codes are used only once, to set up the authenticator
  app. Device trust is bound to one network and invalidated by a password change.
- **Brute-force protection:** lockouts per account and per IP address, rate limits for sign-in and for all requests,
  single-use codes with limited validity and attempts, a honeypot field for bots.
- **Sessions:** `__Host-` cookies (`HttpOnly`, `Secure`, `SameSite`), idle and absolute timeouts, HSTS, regular
  re-validation of the account against the directory.
- **Browser hardening:** strict Content-Security-Policy without inline scripts, antiforgery tokens, framing disabled,
  no caching of pages, server headers removed, generic error pages.
- **Data at rest:** authenticator secrets and integration tokens encrypted with ASP.NET Core Data Protection (optionally
  protected with Windows DPAPI); one-time codes and device tokens stored only as hashes. Permissions are stored by
  SID, so renaming accounts or groups does not break them.
- **Least privilege:** dedicated database logins with only the rights the application needs; the administration panel
  and selected modules can be limited to the company network.
- **Auditing:** sign-ins, permission changes and writes to external systems are recorded in the audit log.

## User manual

The manual for employees lives in the intranet (*Pomoc → Instrukcja*) and as a PDF. Both are generated from one HTML
source; all screenshots are taken in demo mode with made-up data.

## Licence

Copyright © 2026 Krzysztof Kurowski. All rights reserved.

This software is proprietary. It was developed on commission for Gąska and may be used only by Gąska for its internal
purposes. Any other use, copying, modification or distribution is prohibited. See [LICENSE](LICENSE) for the full
terms. Third-party components keep their own licences (e.g. the Tesseract language data under Apache 2.0).
