// Local architecture regressions for the native embedded Live player. No WebView or provider network.
const assert = require('node:assert/strict');
const { readFileSync } = require('node:fs');
const { resolve } = require('node:path');
const { test } = require('node:test');

const read = path => readFileSync(resolve(__dirname, '../..', path), 'utf8');
const xaml = read('src/IPTVExplorer.Desktop/MainWindow.xaml');
const main = read('src/IPTVExplorer.Desktop/MainWindow.xaml.cs');
const manager = read('src/IPTVExplorer.Desktop/PlayerWindowManager.cs');
const coordinator = read('src/IPTVExplorer.Player/PlaybackCoordinator.cs');
const app = read('src/IPTVExplorer.Desktop/App.xaml.cs');

test('MainWindow owns a native Live surface and all required controls', () => {
  assert.match(xaml, /NativeVideoHost x:Name="LiveVideoHost"/);
  for (const name of ['LivePlayerTitle', 'LivePlayPauseButton', 'LiveVolumeSlider', 'LiveFullscreenButton', 'LiveAudioTracks', 'LiveSubtitleTracks']) assert.match(xaml, new RegExp(`x:Name="${name}"`));
  assert.match(xaml, /Click="OnLiveStop"/);
});

test('WebView2 and libmpv use separate WPF sibling columns to avoid HWND airspace overlap', () => {
  assert.match(xaml, /BrowserHost" Grid.Column="0"/);
  assert.match(xaml, /LivePlayerPane" Grid.Column="1"/);
  assert.match(xaml, /non-overlapping grid columns avoid airspace\/z-order conflicts/);
});

test('Live uses MainWindow while VOD and Series retain PlayerWindow', () => {
  assert.match(manager, /reference\.MediaType == CatalogType\.Live/);
  assert.match(manager, /ShowIntegratedLivePlayerAsync\(liveTitles\.Find\(reference\), cancellationToken\)/);
  assert.match(manager, /return await ShowWindowCoreAsync\(cancellationToken\)/);
  assert.match(manager, /new PlayerWindow\(player\)/);
  assert.match(coordinator, /_windows\.ShowAsync\(reference, cancellationToken\)/);
});

test('the singleton libmpv engine backs both native surfaces and fullscreen stays native', () => {
  assert.match(app, /AddSingleton<IPlayerService, LibMpvPlayerService>/);
  assert.match(app, /GetRequiredService<IPlayerService>\(\)/);
  assert.match(main, /TrueFullscreenBehavior _liveFullscreenBehavior/);
  assert.match(main, /BrowserHost\.Visibility = Visibility\.Collapsed/);
  assert.match(main, /_player\.SetFullscreen\(fullscreen\)/);
  assert.doesNotMatch(main, /Process\.Start\([^)]*(?:mpv|vlc)/i);
});

test('Live fullscreen is video-first and restores the embedded layout on exit', () => {
  for (const name of ['LiveHeaderPanel', 'LiveControlsPanel', 'LiveTracksPanel', 'LiveStatusBadge', 'LiveVideoFrame', 'LiveFullscreenControlsPopup']) {
    assert.match(xaml, new RegExp(`x:Name="${name}"`));
  }
  assert.match(main, /LiveHeaderPanel\.Visibility = Visibility\.Collapsed/);
  assert.match(main, /LiveControlsPanel\.Visibility = Visibility\.Collapsed/);
  assert.match(main, /LiveTracksPanel\.Visibility = Visibility\.Collapsed/);
  assert.match(main, /LiveVideoFrame\.CornerRadius = new CornerRadius\(0\)/);
  assert.match(main, /LiveFullscreenControlsPopup\.IsOpen = true/);
  assert.match(main, /LiveHeaderPanel\.Visibility = Visibility\.Visible/);
  assert.match(main, /LivePlayerPane\.Margin = new Thickness\(0, _liveSurfaceTop, 0, 0\)/);
});
