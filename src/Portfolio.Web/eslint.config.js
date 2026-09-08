// @ts-check
/*
 * ESLint flat config (the only form ESLint 10 supports).
 *
 * Scope: this lints *conventions*, not formatting. Prettier owns formatting
 * (`.prettierrc`) and no stylistic/layout rule is enabled here, so the two
 * never disagree and there is no need for eslint-config-prettier.
 *
 * Type-aware linting is on (`parserOptions.projectService`) because
 * `no-floating-promises` needs types. That makes lint slower than a syntactic
 * run; it is the price of the rules that actually catch things.
 */

const tseslint = require('typescript-eslint');
const angular = require('angular-eslint');

module.exports = tseslint.config(
  {
    ignores: ['dist/**', '.angular/**', 'coverage/**', 'node_modules/**', 'public/**'],
  },

  // ---------------------------------------------------------------- TypeScript
  {
    files: ['**/*.ts'],
    extends: [
      ...tseslint.configs.recommended,
      ...tseslint.configs.stylistic,
      ...angular.configs.tsRecommended,
    ],
    processor: angular.processInlineTemplates,
    languageOptions: {
      parserOptions: {
        projectService: {
          // Nothing outside src/ is in tsconfig.app/spec; allow those few
          // config files to be linted against the default project.
          allowDefaultProject: ['*.js', '*.ts'],
        },
        tsconfigRootDir: __dirname,
      },
    },
    rules: {
      // --- The conventions this project already holds, now machine-checked ---

      // Zero `any` outside specs today. Lock it in.
      '@typescript-eslint/no-explicit-any': 'error',

      // 23/23 components are OnPush. Note the Angular 22 semantics: OnPush is
      // now the *default*, so this rule flags components that opt OUT
      // (`ChangeDetectionStrategy.Eager`, or the deprecated `.Default`).
      // `allowExplicitOnPush: true` keeps the explicit
      // `changeDetection: ChangeDetectionStrategy.OnPush` this codebase writes
      // on all 23 components legal — it is redundant under v22 but it is also
      // the documented house convention, and stripping it would be 23 files of
      // churn for no behaviour change. Verified to fire on an `Eager` opt-out.
      '@angular-eslint/prefer-on-push-component-change-detection': [
        'error',
        { allowExplicitOnPush: true },
      ],

      // `use-lifecycle-interface` ships as a warning; this project treats it as
      // a hard rule.
      '@angular-eslint/use-lifecycle-interface': 'error',
      '@angular-eslint/no-empty-lifecycle-method': 'error',

      // Selector conventions as actually practised: components are `app-`
      // prefixed kebab-case *elements* (`app-state-message`, `app-shell`); the
      // sole directive is an `app`-prefixed camelCase *attribute*
      // (`appVirtualRowgroup` in shared/table/virtual-rowgroup.ts).
      '@angular-eslint/component-selector': [
        'error',
        { type: 'element', prefix: 'app', style: 'kebab-case' },
      ],
      '@angular-eslint/directive-selector': [
        'error',
        { type: 'attribute', prefix: 'app', style: 'camelCase' },
      ],

      // Needs type information. Zero hits today.
      //
      // NOTE for F7 (unmanaged subscriptions): this rule does NOT cover them.
      // A `Subscription` is not a Thenable, so nothing in eslint /
      // typescript-eslint / angular-eslint fires on
      // `http.delete(...).subscribe(...)`. That would need a fourth plugin
      // (`eslint-plugin-rxjs-x`, rule `no-ignored-subscription`), which would
      // land ~27 errors across the app. Left as a follow-up under F7/F1 rather
      // than shipping a red lint.
      '@typescript-eslint/no-floating-promises': 'error',

      // `takeUntilDestroyed()` outside an injection context must be passed a
      // DestroyRef explicitly — the silent-no-op trap.
      '@angular-eslint/no-implicit-take-until-destroyed': 'error',

      // --- Deliberately off ---------------------------------------------------

      // OFF: `@typescript-eslint/consistent-type-definitions` (from
      // `stylistic`). Dialog contracts are declared as `type` aliases because
      // several of them are unions that *cannot* be interfaces
      // (`AssetFormDialogData = {mode:'create'} | {mode:'edit'; asset}`).
      // Forcing the single-shape siblings next to them into `interface` would
      // split one contract across two declaration forms, which reads worse
      // than the uniformity the rule is after.
      '@typescript-eslint/consistent-type-definitions': 'off',
    },
  },

  // Specs and test doubles. `no-explicit-any` stays ON here — the existing
  // `eslint-disable-next-line` comments in the suite are deliberate, narrow and
  // already in place, so the rule is doing real work rather than nagging.
  {
    files: ['**/*.spec.ts', 'src/test-setup.ts', 'src/app/**/testing/**/*.ts'],
    rules: {
      // Stubs are meant to be empty: `src/test-setup.ts`'s canvas 2D context
      // double is ~20 no-op methods, and `jest.fn()`-style empty arrows stand
      // in for dialog/router collaborators.
      '@typescript-eslint/no-empty-function': 'off',
      '@typescript-eslint/no-non-null-assertion': 'off',
      // The OnPush/selector conventions are about app code; specs declare
      // throwaway host components.
      '@angular-eslint/prefer-on-push-component-change-detection': 'off',
      '@angular-eslint/component-selector': 'off',
      '@angular-eslint/directive-selector': 'off',
    },
  },

  // --------------------------------------------------------------- Templates
  {
    files: ['**/*.html'],
    extends: [...angular.configs.templateRecommended, ...angular.configs.templateAccessibility],
    rules: {},
  },
);
