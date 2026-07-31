# PortfolioWeb

This project was generated using [Angular CLI](https://github.com/angular/angular-cli) version 22.1.2.

## Design system

Every colour, spacing value, radius, shadow, font size, z-index and motion curve lives in
`src/styles/ui.tokens.scss` as a CSS custom property, with `src/styles/ui.mixins.scss` holding the
small set of breakpoint/elevation/focus SCSS helpers layered on top. **New UI reads tokens; it does
not introduce values** — a component must never hardcode a hex colour or a raw `px` spacing value.
Angular Material's theme in `src/styles.scss` is derived from these tokens (see the comment block
there), not configured with a second, independent set of colours. The Stocks/Crypto section accent
is a token swap driven by `data-section="stock" | "crypto"` on `<html>`, set by `AppShell` from the
active route's `data.assetClass` — never by forking component styles per section.

Both partials are on `stylePreprocessorOptions.includePaths` (see `angular.json`), so any component
can `@use 'ui.mixins' as ui;` from anywhere without a relative path.

## Dev proxy

`ng serve` proxies `/api` and `/hubs` to the backend at `http://localhost:5100` (see
`proxy.conf.json` and `src/Portfolio.Api/Properties/launchSettings.json`) — the API has no CORS
policy, so this proxy is required, not optional. Services call relative URLs only
(`core/api/api-routes.ts`); never hardcode the API's origin.

## Node version note

The Angular 22 CLI hard-requires Node `^22.22.3 || ^24.15.0 || >=26.0.0` and refuses to run below
that. This workspace's pinned dev environment is Node 22.13.0, a fully capable Node 22 LTS release
that is otherwise compatible — the CLI's gate is stricter than the actual runtime requirement.
`scripts/patch-ng-cli-node-check.js` widens that check in the local `@angular/cli` install and runs
automatically via the `postinstall` hook after every `npm install`, so a clean install never
regresses it. Delete both once the machine runs a supported Node version — they become a no-op.

## Development server

To start a local development server, run:

```bash
ng serve
```

Once the server is running, open your browser and navigate to `http://localhost:4200/`. The application will automatically reload whenever you modify any of the source files.

## Code scaffolding

Angular CLI includes powerful code scaffolding tools. To generate a new component, run:

```bash
ng generate component component-name
```

For a complete list of available schematics (such as `components`, `directives`, or `pipes`), run:

```bash
ng generate --help
```

## Building

To build the project run:

```bash
ng build
```

This will compile your project and store the build artifacts in the `dist/` directory. By default, the production build optimizes your application for performance and speed.

## Running unit tests

To execute unit tests with the [Vitest](https://vitest.dev/) test runner, use the following command:

```bash
ng test
```

## Running end-to-end tests

For end-to-end (e2e) testing, run:

```bash
ng e2e
```

Angular CLI does not come with an end-to-end testing framework by default. You can choose one that suits your needs.

## Additional Resources

For more information on using the Angular CLI, including detailed command references, visit the [Angular CLI Overview and Command Reference](https://angular.dev/tools/cli) page.
