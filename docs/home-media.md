# Home recent media and decorative backgrounds

The home bridge reads at most 20 films and 20 series from the active provider's local index. Each query filters its catalog before applying the limit and sorts by provider date descending, then title and remote ID. A missing date excludes the item. Empty sections are hidden. Both carousels reuse the existing detail dialog and its playback/download actions.

## Inspected provider fields

A limited read-only sample of the configured Xtream provider showed:

- Film list: `stream_icon`, `added`.
- Film detail: `info.movie_image`, `info.backdrop`, and an empty `info.backdrop_path` array in the sampled response.
- Series list and detail: `cover`, `backdrop_path` arrays, `last_modified`.

These are observations of a small sample, not guarantees for every provider or item. The implementation accepts `backdrop_path` (array or string) and `backdrop` when they contain an eligible absolute image URL. It does not invent `cover_big`, `fanart`, `background` or other unobserved sources.

There was no configured Stalker provider available for a live sample. Inspection of the adapter and fixtures shows `screenshot_uri`, `cover` and `logo` as poster sources. Stalker home novelty sections remain disabled because dates/list ordering have not been established as reliable.

## Dates and artwork selection

Films use valid `added`, `added_at` or `created_at` values. Xtream series also accept `last_modified` when no valid addition date is supplied. This represents provider recency, including updates to an existing series, not a claim about its original creation date. The series heading tooltip explains this. Release years and the local rebuild time never become novelty dates.

The normal rebuild captures artwork already present in catalog lists and never requests individual series details. The home also never requests details to repair posters. A film backdrop available only in its detail endpoint is not fetched for decoration; its indexed poster is the fallback.

Background candidates start with the first continue-watching poster, then up to six recent films and six recent series. Per content, a safe backdrop precedes the safe poster. Invalid, failed or timed-out images are skipped; failure of a backdrop tries its poster. With no usable image, the neutral background remains intact. No external metadata API is added.

## Storage and URL policy

Database migration 4 marks VOD/series index policies dirty once, in a transaction. It does not alter any open search index. The next normal atomic rebuild adds nullable `search_documents.backdrop_url` and a catalog/date/title/ID index. Old indexes remain readable with a null backdrop. `SearchIndexFileAccess`, scoped readers/connections, targeted `ClearPool`, and the existing atomic replacement sequence are preserved. `playback_history` and resume policy are unchanged.

Decorative backgrounds retain the unchanged `HomeArtwork.SafeUrl` policy: absolute HTTP(S) static image URLs only, no user-info, queries or fragments. Card/detail posters use `MediaArtwork.SafeImageUrl`, which also accepts public CDN transformation queries and extensionless image endpoints. Both reject credential markers (including encoded values), playback paths, MAC addresses and known provider secrets when supplied. Index construction, detail/cache writes and home responses validate posters with the provider secret; cached and legacy-index images are checked again before delivery. Unsafe artwork becomes null, never a rewritten URL. The background candidate selection still applies its stricter policy to poster fallbacks too. Image requests have no referrer; no provider secret crosses the home bridge.

## Passive series artwork cache — no automated provider requests

The real provider must never be used for automated tests or live diagnostics. Use in-memory HTTP handlers, anonymized JSON fixtures and temporary local databases only. Do not run the old diagnostic helpers against a configured provider.

`RecentSeriesArtwork.ApplyCachedAsync` only reads local `series_artwork` entries for the recent series. It has no provider-client dependency, background tasks, network concurrency limiter, timeout budget or retry schedule. A safe cached poster takes precedence over the indexed poster; otherwise the safe indexed poster or placeholder is used. The home dashboard is local-only as well: activation is shown as locally enabled, with network availability explicitly unverified. It no longer checks the account on every home visit.

The only production cache-writing call site is `BridgeRouter.SeriesDetail`: after the user's ordinary `GetSeriesDetailsAsync` request returns, it validates `cover`/`movie_image` and passes the existing response poster to `RecentSeriesArtwork.RememberDetailAsync`. No second request is issued. Missing/unsafe artwork does not erase a previously safe cache entry; optional cache write failures do not hide the requested detail or initiate any request. Subsequent home renders reuse that saved poster, including after an index rebuild. Existing null cache rows have no retry semantics.

`series_artwork` remains in the application database, keyed by provider/series and cascading on provider deletion, separate from replaceable search files. No new search-index writer is introduced, and `SearchIndexFileAccess`, pool clearing and atomic replacement are unchanged. Rebuilds use only their ordinary catalog endpoints; they neither fetch details nor populate the artwork cache.

An image load error only removes the broken image and shows the existing placeholder. The `home.seriesArtwork` RPC and its DOM error callback were removed. No repair request follows a 404, 403, 429, 520, timeout or network failure. The shared HTTP retry behavior for normal explicit user actions is unchanged; artwork adds none.

Tests exercise the production bridge with fixture-only HTTP handlers: repeated/concurrent homes and dashboard reads create no provider client; a user detail request populates the cache; provider errors trigger only the existing transport retries; rebuilds issue no detail request. Local DOM regressions run with `node --test tests/ui/home-artwork.test.cjs`, executing the production rendering functions against a minimal DOM double without browser/network access.

## Presentation and checks

Recent cards use a non-wrapping horizontal flex track with native overflow, proximity snapping, Shift+wheel support, focusable cards/track and previous/next buttons advancing four cards. Buttons disable at the ends. Width changes reveal fewer or more fixed-width cards without adding rows.

Two decorative DOM image layers occupy only the main grid column and ignore pointer events. The backdrop sits at stacking level 0, below the existing main/sidebar content at level 1. Images use cover, 7 px blur, brightness .56, saturation .90, active opacity .74 and scale 1.03 to make the background image more recognizable while retaining a soft, dark presentation. The horizontal dark gradient uses 58%/26%/42% opacity, with a separate bottom fade; this keeps the image perceptible without changing card backgrounds. A 15-second single timer starts a preloaded opacity crossfade lasting 1.8 seconds. Timers/preloads stop when leaving home or hiding the document. Reduced motion disables rotation and fades.

Backend regressions cover bounded sorting, provider/catalog isolation, undated data, provider parsing, safe/fallback artwork, legacy index replacement after pooled reads, and idempotent invalidation. A local browser preview with synthetic data was checked at 1920, 800 and 600 px: one row per carousel, four-card arrows, keyboard endpoints, image-failure fallback, main-only backdrop, and existing film/series dialogs with playback/download actions. This preview does not constitute end-to-end playback testing in WebView2.

The backdrop visibility follow-up was checked at 1920, 1366 and 800 px with only synthetic portrait posters: successful loading into both layers, a perceptible blurred background, the 1.8-second opacity crossfade, independent sidebar, no page-width overflow, and unobstructed card/quick-link clicks. No carousel, artwork filtering or rotation logic was changed.
