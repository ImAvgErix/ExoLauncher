namespace ExoLauncher.Helpers;

/// <summary>
/// Local presentation changes on the music web players only. While the room
/// hides the page (<c>data-exo-background</c>) YouTube Music's motion stops;
/// Apple Music's whole page is skipped by layout and paint
/// (content-visibility), so nothing of it is drawn or decoded while it plays
/// behind Exo's own player; its &lt;audio&gt; element plays on.
/// </summary>
internal static class EmbeddedClientPresentation
{
    internal const string Script = """
        (() => {
          if (window.top !== window || location.protocol !== 'https:' ||
            location.hostname !== 'music.youtube.com' && location.hostname !== 'music.apple.com') return;
          const css = location.hostname === 'music.apple.com' ? `
            html[data-exo-background] body > :not(audio):not(video):not(script):not(style) { content-visibility:hidden!important; }
            html[data-exo-background] *, html[data-exo-background] *::before, html[data-exo-background] *::after {
              animation-play-state:paused!important; transition:none!important;
            }
          ` : `
            html[data-exo-background] *, html[data-exo-background] *::before, html[data-exo-background] *::after {
              animation-play-state:paused!important; transition:none!important; caret-color:transparent!important;
            }
            html, ytmusic-app {
              --ytmusic-background:#000!important;
              --ytmusic-general-background-a:#000!important;
              --ytmusic-general-background-b:#000!important;
              --ytmusic-general-background-c:#000!important;
              --ytmusic-nav-bar:#000!important;
              --ytmusic-player-bar-background:#000!important;
              --ytmusic-player-page-background:#000!important;
              --ytmusic-menu-popup-background:#000!important;
              --ytmusic-dialog-background-color:#000!important;
              --yt-spec-base-background:#000!important;
              --yt-spec-raised-background:#000!important;
              /* Raised surfaces (menus, dialogs, search suggestions, cards)
                 are #0d0d0d with a #1c1c1c hairline, hover #141414; the
                 vendor's #212121-#333 greys and 5-20% white washes go.
                 additive-background stays: it is also the seek bar's track. */
              --ytmusic-brand-background-solid:#0d0d0d!important;
              --paper-listbox-background-color:#0d0d0d!important;
              --paper-menu-background-color:#0d0d0d!important;
              --paper-dialog-background-color:#0d0d0d!important;
              --dark-theme-background-color:#0d0d0d!important;
              --ytmusic-search-background:#0d0d0d!important;
              --ytmusic-dropdown-background:#0d0d0d!important;
              --ytmusic-dropdown-item-hover-background:#141414!important;
              --ytmusic-dropdown-border-color:#1c1c1c!important;
              --ytmusic-menu-item-hover-background-color:#141414!important;
              --ytmusic-search-suggestion-focus-active-background:#141414!important;
              --ytmusic-guide-hover:#141414!important;
              --ytmusic-divider:#1c1c1c!important;
              --ytmusic-guide-divider:#1c1c1c!important;
              --dark-theme-divider-color:#1c1c1c!important;
              --ytmusic-confirm-dialog-outline:#242424!important;
              --ytmusic-item-section-info-panel-overview-background:#0d0d0d!important;
              --ytmusic-explore-chip-background:#0d0d0d!important;
              --ytmusic-badge-chip-background:#0d0d0d!important;
              --ytmusic-badge-chip-inactive-hover:#141414!important;
              --ytmusic-search-bar-background-bauhaus:#000!important;
              --ytmusic-search-bar-border-bauhaus:#242424!important;
              --ytmusic-search-border:#242424!important;
              --ytmusic-responsive-list-item-checked-background-bauhaus:#1c1c1c!important;
              --ytmusic-multiselect-form-item-hover-background-color:#141414!important;
              --ytmusic-color-white-opacity-0-05:#141414!important;
              --ytmusic-color-white1-alpha10:#1c1c1c!important;
              --yt-sys-color-baseline--base-background:#000!important;
              --yt-sys-color-baseline--raised-background:#0d0d0d!important;
              --yt-sys-color-baseline--menu-background:#0d0d0d!important;
              --yt-sys-color-baseline--tonal-background:#141414!important;
              --yt-sys-color-baseline--mono-tonal-hover:#242424!important;
              --yt-sys-color-baseline--button-chip-background-hover:#1c1c1c!important;
              --yt-sys-color-baseline--mono-filled-hover-inverse:#141414!important;
              --yt-sys-color-baseline--outline:#242424!important;
              --yt-sys-color-baseline--outline-opaque:#242424!important;
              --yt-sys-color-baseline--frosted-glass-desktop:#000!important;
              --yt-sys-color-baseline--static-black:#000!important;
              --yt-sys-color-baseline--solid-background-inverse:#000!important;
              --yt-sys-color-baseline--overlay-solid-background-inverse:#000!important;
              --yt-sys-color-baseline--overlay-additive-background:rgba(0,0,0,.6)!important;
              --yt-sys-color-baseline--scrim-background-gradient-2:rgba(0,0,0,.3)!important;
              --yt-sys-color-baseline--scrim-background-gradient-3:rgba(0,0,0,.6)!important;
              --yt-sys-color-baseline--scrim-background-gradient-4:rgba(0,0,0,.9)!important;
              --yt-sys-color-baseline--scrim-background-gradient-5:#000!important;
              --yt-deprecated-general-background-a:#000!important;
              --yt-deprecated-general-background-b:#000!important;
              --yt-deprecated-general-background-c:#000!important;
              --yt-deprecated-dark-surface-100:#000!important;
            }
            /* Surfaces the vendor paints with fixed greys, not tokens. */
            ytmusic-dialog { background-color:#0d0d0d!important; box-shadow:inset 0 0 0 1px #1c1c1c!important; }
            ytmusic-menu-popup-renderer, ytmusic-popup-container tp-yt-paper-dialog,
            ytmusic-search-suggestions-section { box-shadow:inset 0 0 0 1px #1c1c1c!important; }
            .category-menu-item.iron-selected.ytmusic-settings-page { background:#1c1c1c!important; }
            .category-menu-item.ytmusic-settings-page:hover,
            .category-menu-item.ytmusic-settings-page:focus:not(.iron-selected) { background:#141414!important; }
            ytmusic-card-shelf-renderer .card-content-container::before,
            ytmusic-card-shelf-renderer #undercards::before { background:#0d0d0d!important; }
            ytmusic-card-shelf-renderer .card-container,
            ytmusic-navigation-button-renderer button.ytmusic-navigation-button-renderer { box-shadow:inset 0 0 0 1px #1c1c1c; }
            /* Unselected chips are Exo's control: black with a hairline. */
            ytmusic-chip-cloud-chip-renderer:is([chip-style="STYLE_DEFAULT"],[chip-style="STYLE_LARGE_TRANSLUCENT_AND_SELECTED_WHITE"]):not([is-selected]):not(.iron-selected) a.ytmusic-chip-cloud-chip-renderer {
              background:#000!important; box-shadow:inset 0 0 0 1px #242424!important;
            }
            ytmusic-chip-cloud-chip-renderer:is([chip-style="STYLE_DEFAULT"],[chip-style="STYLE_LARGE_TRANSLUCENT_AND_SELECTED_WHITE"]):not([is-selected]):not(.iron-selected) a.ytmusic-chip-cloud-chip-renderer:hover {
              background:#141414!important;
            }
            ytmusic-app, ytmusic-nav-bar, ytmusic-player-bar, ytmusic-player-page,
            ytmusic-browse-response, ytmusic-search-page, ytmusic-library-page,
            ytmusic-guide-renderer, ytmusic-menu-popup-renderer, ytmusic-popup-container tp-yt-paper-dialog,
            ytmusic-player-queue, ytmusic-tab-renderer, #layout, #guide-wrapper, #nav-bar-background,
            ytmusic-immersive-header-renderer, ytmusic-player#player {
              background-color:#000!important;
              background-image:none!important;
            }
            ytmusic-app #background, ytmusic-player-page #background, ytmusic-player-page .background-gradient {
              background:#000!important;
            }
            /* Casting has no target from an embedded player. */
            ytmusic-cast-button, ytmusic-nav-bar .cast-button, #cast-button { display:none!important; }
            html,body { background-color:#000!important; }
            * { scrollbar-width:none!important; }
            *::-webkit-scrollbar { display:none!important; width:0!important; height:0!important; }
            /* Glass layers on hundreds of offscreen controls stall Chromium's
               layer construction. Keep the existing dark surfaces opaque. */
            *, *::before, *::after { backdrop-filter:none!important; -webkit-backdrop-filter:none!important; }
            /* Let the browser scroll directly and make frequently used controls
               react immediately, while preserving loading/progress animations. */
            * { scroll-behavior:auto!important; }
            button, a, input, [role="button"], [role="tab"], [role="slider"] { transition-duration:0s!important; }
            /* YouTube Music's optimization (OptimizeScript sets the
               attribute): Premium upsells go, and a video the room no longer
               shows stops painting while the song keeps playing. */
            html[data-exo-optimize] ytmusic-mealbar-promo-renderer,
            html[data-exo-optimize] ytmusic-statement-banner-renderer,
            html[data-exo-optimize] tp-yt-paper-dialog:has(ytmusic-mealbar-promo-renderer),
            html[data-exo-optimize] ytmusic-guide-entry-renderer:has(a[href*="music_premium"]),
            html[data-exo-optimize] ytmusic-pivot-bar-item-renderer[tab-id="SPunlimited"],
            html[data-exo-optimize] a[href*="music_premium"],
            html[data-exo-optimize] ytmusic-popup-container [role="menuitem"]:has(.primary-entry-badge-icon[aria-label="Premium"]) { display:none!important; }
            /* No color washes: the full-bleed artwork behind home, playlist
               and album pages (blurred 80px on the latter) and behind the
               search page's top result, and the page-long gradient over it.
               The artist photo keeps its fade to black (the scrim its name
               reads on), and the scrims on thumbnails stay. */
            html[data-exo-optimize] #background.immersive-background { display:none!important; }
            html[data-exo-optimize] ytmusic-browse-response .background-gradient,
            html[data-exo-optimize] ytmusic-player-page .background-gradient { background:none!important; }
            /* The player page paints the song's artwork, blurred, on its own ::before layer. */
            html[data-exo-optimize] ytmusic-player-page::before, html[data-exo-optimize] ytmusic-player-page::after { background:none!important; display:none!important; }
            /* Exo's controls: a black cut-out with a hairline. */
            html[data-exo-optimize] ytmusic-search-box .search-box {
              background:#000!important; border:1px solid #242424!important; box-sizing:border-box;
            }
            html[data-exo-optimize][data-exo-background] ytmusic-player video { visibility:hidden!important; }
            /* Static motion, nested under the attribute (see StaticMotion). */
            html[data-exo-optimize] {
        """ + "\n" + StaticMotion.Css + """

            }
          `;
          let active = true;
          window.__exoClientPresentation = {
            setActive(value) {
              active = value === true;
              document.documentElement?.toggleAttribute('data-exo-background', !active);
            },
          };
          const apply = () => {
            if (!document.head || document.getElementById('exo-client-presentation')) return;
            document.documentElement.toggleAttribute('data-exo-background', !active);
            const style = document.createElement('style');
            style.id = 'exo-client-presentation';
            style.textContent = css;
            document.head.appendChild(style);
          };
          document.readyState === 'loading'
            ? document.addEventListener('DOMContentLoaded', apply, {once:true}) : apply();
        })();
        """;

    /// Registered beside <see cref="Script"/> for YouTube Music, which Exo
    /// always optimizes: it sets the attribute the optimize CSS is scoped to.
    internal const string OptimizeScript = """
        (() => {
          if (window.top !== window || location.hostname !== 'music.youtube.com') return;
          // Runs when the document is created, before the parser has made
          // <html>: mark it as soon as it exists, and again once parsed.
          const mark = () => { const root = document.documentElement; if (root) root.setAttribute('data-exo-optimize', ''); return !!root; };
          if (!mark()) new MutationObserver((_, observer) => { if (mark()) observer.disconnect(); }).observe(document, { childList: true });
          document.addEventListener('DOMContentLoaded', mark, { once: true });
        })();
        """;
}
