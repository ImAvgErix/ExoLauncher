// Exo's markup rules for Exo Mouse (CSS: scripts/css-design.mjs). Types are tsc's job.
import tseslint from 'typescript-eslint'

export default tseslint.config({
  files: ['src/**/*.{ts,tsx}'],
  languageOptions: { parser: tseslint.parser },
  rules: {
    'no-restricted-syntax': ['error',
      {
        // Lowercase only: a component's own `title` prop (Panel, Centered) is a heading, not a tooltip.
        selector: "JSXOpeningElement[name.name=/^[a-z]/] > JSXAttribute[name.name='title']",
        message: 'No native title= tooltips. Name the control with aria-label.',
      },
      {
        selector: "JSXAttribute[name.name='style'] Property[key.name='fontSize']",
        message: 'Font size lives in mouse.css on the type scale (--type-title / --type-body / --type-small), not inline.',
      },
    ],
  },
})
