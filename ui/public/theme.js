// Appearance before the first frame (src/lib/appearance.ts): the last choice, Match Windows when there is none.
try {
  var chosen = localStorage.getItem('exo.appearance')
  var theme = chosen === 'light' || chosen === 'dark' || chosen === 'void' ? chosen
    : matchMedia('(prefers-color-scheme: light)').matches ? 'light' : 'dark'
  document.documentElement.dataset.exoTheme = theme
} catch (e) {
  document.documentElement.dataset.exoTheme = 'dark'
}
