// Local source regressions for the native embedded Live player. No WebView or provider network.
const assert = require('node:assert/strict');
const { readFileSync } = require('node:fs');
const { resolve } = require('node:path');
const { test } = require('node:test');

const mainXaml = readFileSync(resolve(__dirname, '../../src/IPTVExplorer.Desktop/MainWindow.xaml'), 'utf8');
const mainCs = readFileSync(resolve(__dirname, '../../src/IPTVExplorer.Desktop/MainWindow.xaml.cs'), 'utf8');
const manager = readFileSync(resolve(__dirname, '../../src/IPTVExplorer.Desktop/PlayerWindowManager.cs'), 'utf8');
const coordinator = readFileSync(resolve(__dirname, '../../src/IPTVExplorer.Player/PlaybackCoordinator.cs'), 'utf8');
const app = readFileSync(resolve(__dirname, '../../src/IPTVExplorer.Desktop/App.xaml.cs'), 'utf8');

test('MainWindow owns a native Live video surface and local controls', () => {
  assert.match(mainXaml, /NativeVideoHost x:Name="LiveVideoHost"/);
  assert.match(mainXaml, /x:Name="LivePlayPauseButton"/);
  assert.match(mainXaml, /Click="OnLiveStop"/);
  assert.match(mainXaml, /x:Name="LiveVolumeSlider"/);
  assert.match(mainXaml, /x:Name="LiveFullscreenButton"/);
  assert.match(mainXaml, /x:Name="LiveAudioTracks"/);
  assert.match(mainXaml, /x:Name="LiveSubtitleTracks"/);
});

test('Live media routes to the integrated surface while non-Live keeps PlayerWindow', () => {
  assert.match(manager, /reference\.MediaType == CatalogType\.Live/);
  assert.match(manager, /ShowIntegratedLivePlayerAsync\(reference, cancellationToken\)/);
  assert.match(manager, /HideIntegratedLivePlayer\(stopPlayback: false\)/);
  assert.match(manager, /new PlayerWindow\(player\)/);
});

test('PlaybackCoordinator passes the opaque MediaReference to the surface selector', () => {
  assert.match(coordinator, /Task<nint> ShowAsync\(MediaReference reference/);
  assert.match(coordinator, /_windows\.ShowAsync\(reference, cancellationToken\)/);
});

test('embedded Live fullscreen is owned by the WPF main window and reuses the same native surface', () => {
  assert.match(mainCs, /TrueFullscreenBehavior _liveFullscreenBehavior/);
  assert.match(mainCs, /BrowserHost\.Visibility = Visibility\.Collapsed/);
  assert.match(mainCs, /LivePlayerColumn\.Width = new GridLength\(1, GridUnitType\.Star\)/);
  assert.match(mainCs, /_player\.SetFullscreen\(fullscreen\)/);
  assert.doesNotMatch(mainCs, /Process\.Start\([^)]*(?:mpv|vlc)/i);
});

test('MainWindow receives the singleton native player service from DI', () => {
  assert.match(app, /GetRequiredService<IPlayerService>\(\)/);
  assert.match(mainCs, /private readonly IPlayerService _player/);
});
