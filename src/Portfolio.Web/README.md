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

## Node version

The Angular 22 CLI refuses to run on anything outside `^22.22.3 || ^24.15.0 || >=26.0.0`, and it is
a hard error, not a warning. That range is mirrored in `package.json`'s `engines` field so a wrong
Node fails at install time with a clear message rather than at the first `ng` invocation.

Dev is on **Node 22.23.2**, deliberately staying on the 22 line rather than jumping to the 24 LTS,
so it matches the `node:22-alpine` base image the Phase 10 container build uses — one Node major
across dev and the container. If you bump the container image, keep it at 22.22.3 or newer.

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
