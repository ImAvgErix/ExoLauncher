import { memo, useState } from 'react'
import { host, type LauncherSettings } from '../../lib/host'
import { Choice } from '../settings/Choice'
import { Row, Section, SwitchRow } from './parts'

/** Null when the save took, else why not. */
type SaveSettings = (patch: Partial<LauncherSettings>) => Promise<string | null>

const TEXT_SIZES = [
  { value: 'small', label: 'Small' },
  { value: 'normal', label: 'Normal' },
  { value: 'large', label: 'Large' },
] as const

const GLASS = [
  { value: 'clear', label: 'Clear' },
  { value: 'tinted', label: 'Tinted' },
  { value: 'solid', label: 'Solid' },
] as const

export const GeneralSection = memo(function GeneralSection({ settings, onSave: save, onLibraryChanged }: {
  settings: LauncherSettings | null
  onSave: SaveSettings
  onLibraryChanged: () => void
}) {
  const [picking, setPicking] = useState(false)
  const [folderError, setFolderError] = useState<string | null>(null)
  const [textError, setTextError] = useState<string | null>(null)
  const [glassError, setGlassError] = useState<string | null>(null)
  const disabled = !settings
  const root = settings?.defaultInstallRoot?.trim() || null

  async function browse() {
    if (picking) return
    setPicking(true)
    setFolderError(null)
    try {
      const result = await host.pickFolder('Choose where new games install')
      if (result.ok && result.path) setFolderError(await save({ defaultInstallRoot: result.path }))
      else if (!result.ok && !result.cancelled) setFolderError(result.message ?? 'Could not open the folder picker.')
    } catch (error) {
      setFolderError(error instanceof Error ? error.message : 'Could not open the folder picker.')
    } finally {
      setPicking(false)
    }
  }

  return (
    <>
    <Section id="general" title="General">
      <Row label="Text size" labelId="set-text-size" error={textError}>
        <Choice
          label="Text size"
          value={settings?.textSize ?? 'normal'}
          choices={TEXT_SIZES}
          disabled={disabled}
          onChange={value => { setTextError(null); void save({ textSize: value }).then(setTextError) }}
        />
      </Row>
      <Row label="Glass" labelId="set-glass" detail="How much shows through the bar, menus and the player" error={glassError}>
        <Choice
          label="Glass"
          value={settings?.glass ?? 'clear'}
          choices={GLASS}
          disabled={disabled}
          onChange={value => { setGlassError(null); void save({ glass: value }).then(setGlassError) }}
        />
      </Row>
      <SwitchRow
        id="set-reduce-motion"
        label="Reduce motion"
        checked={settings?.reduceMotion === true}
        disabled={disabled}
        onChange={value => save({ reduceMotion: value })}
      />
      <SwitchRow
        id="set-tools"
        label="Show tools and soundtracks"
        detail="Servers, SDKs and soundtracks"
        checked={settings?.showLibraryTools === true}
        disabled={disabled}
        onChange={async value => {
          const error = await save({ showLibraryTools: value })
          if (!error) onLibraryChanged()
          return error
        }}
      />
    </Section>
    <Section id="downloads" title="Downloads">
      <Row label="Install folder" detail={root ?? "Exo's Games folder"} error={folderError}>
        {root ? (
          <button type="button" className="exo-set-btn" disabled={disabled || picking} onClick={() => { setFolderError(null); void save({ defaultInstallRoot: null }).then(setFolderError) }}>
            Reset
          </button>
        ) : null}
        <button type="button" className="exo-set-btn" disabled={disabled || picking} onClick={() => void browse()}>
          {picking ? 'Choosing…' : 'Change'}
        </button>
      </Row>
      <DownloadRows settings={settings} disabled={disabled} save={save} />
    </Section>
    </>
  )
})

/** Download hours as presets: any time, overnight windows, or the workday. */
const HOURS = [
  { value: 'any', label: 'Any time', start: null, end: null },
  { value: '0-8', label: '12–8 AM', start: 0, end: 8 },
  { value: '1-7', label: '1–7 AM', start: 1, end: 7 },
  { value: '9-17', label: '9 AM–5 PM', start: 9, end: 17 },
] as const

function DownloadRows({ settings, disabled, save }: { settings: LauncherSettings | null; disabled: boolean; save: (patch: Partial<LauncherSettings>) => Promise<string | null> }) {
  const [hoursError, setHoursError] = useState<string | null>(null)
  // Hours set some other way (an older build, the settings file) show as "Set elsewhere", never as Any time.
  const current = HOURS.find(h => h.start === (settings?.downloadHoursStart ?? null) && h.end === (settings?.downloadHoursEnd ?? null))?.value ?? 'custom'
  return (
    <>
      <SwitchRow
        id="set-pause-playing"
        label="Pause downloads while playing"
        detail="Epic, GOG and Amazon. Steam has its own."
        checked={settings?.pauseDownloadsWhilePlaying !== false}
        disabled={disabled}
        onChange={value => save({ pauseDownloadsWhilePlaying: value })}
      />
      <Row label="Download hours" labelId="set-download-hours" detail="New downloads wait for these hours" error={hoursError}>
        <Choice
          label="Download hours"
          value={current}
          choices={HOURS}
          disabled={disabled}
          onChange={value => {
            const pick = HOURS.find(h => h.value === value)!
            setHoursError(null)
            void save({ downloadHoursStart: pick.start, downloadHoursEnd: pick.end }).then(setHoursError)
          }}
        />
      </Row>
      <SwitchRow
        id="set-auto-update"
        label="Update games automatically"
        detail="Epic, GOG and Amazon, while you're away. Steam has its own."
        checked={settings?.autoUpdateGames !== false}
        disabled={disabled}
        onChange={value => save({ autoUpdateGames: value })}
      />
    </>
  )
}
