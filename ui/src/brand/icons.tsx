import type { Icon, IconWeight } from '@phosphor-icons/react'
import { ArrowSquareOutIcon } from '@phosphor-icons/react/dist/csr/ArrowSquareOut'
import { CaretLeftIcon } from '@phosphor-icons/react/dist/csr/CaretLeft'
import { CaretRightIcon } from '@phosphor-icons/react/dist/csr/CaretRight'
import { CheckIcon } from '@phosphor-icons/react/dist/csr/Check'
import { CircleNotchIcon } from '@phosphor-icons/react/dist/csr/CircleNotch'
import { CoffeeIcon } from '@phosphor-icons/react/dist/csr/Coffee'
import { CopySimpleIcon } from '@phosphor-icons/react/dist/csr/CopySimple'
import { DownloadSimpleIcon } from '@phosphor-icons/react/dist/csr/DownloadSimple'
import { FileTextIcon } from '@phosphor-icons/react/dist/csr/FileText'
import { FolderOpenIcon } from '@phosphor-icons/react/dist/csr/FolderOpen'
import { GearSixIcon } from '@phosphor-icons/react/dist/csr/GearSix'
import { MagnifyingGlassIcon } from '@phosphor-icons/react/dist/csr/MagnifyingGlass'
import { MinusIcon } from '@phosphor-icons/react/dist/csr/Minus'
import { PlayIcon } from '@phosphor-icons/react/dist/csr/Play'
import { SquareIcon } from '@phosphor-icons/react/dist/csr/Square'
import { StarIcon } from '@phosphor-icons/react/dist/csr/Star'
import { StopIcon } from '@phosphor-icons/react/dist/csr/Stop'
import { TrashSimpleIcon } from '@phosphor-icons/react/dist/csr/TrashSimple'
import { WrenchIcon } from '@phosphor-icons/react/dist/csr/Wrench'
import { XIcon } from '@phosphor-icons/react/dist/csr/X'

type IconProps = {
  size?: number
  className?: string
}

/** Phosphor: 16px optical grid. Regular for chrome, fill for Play / Stop / pin-on. */
function glyph(IconCmp: Icon, weight: IconWeight = 'regular') {
  return function Glyph({ size = 16, className }: IconProps) {
    return (
      <IconCmp
        size={size}
        weight={weight}
        color="currentColor"
        className={className}
        aria-hidden
        style={{ display: 'block', flexShrink: 0 }}
      />
    )
  }
}

export const Search = glyph(MagnifyingGlassIcon)
export const Settings = glyph(GearSixIcon)
export const Loader2 = glyph(CircleNotchIcon)
export const Minus = glyph(MinusIcon)
export const Maximize = glyph(SquareIcon)
export const Restore = glyph(CopySimpleIcon)
export const Close = glyph(XIcon)
export const Star = glyph(StarIcon)
export const StarFilled = glyph(StarIcon, 'fill')
export const Play = glyph(PlayIcon, 'fill')
export const Stop = glyph(StopIcon, 'fill')
export const Download = glyph(DownloadSimpleIcon)
export const Trash = glyph(TrashSimpleIcon)
export const FolderOpen = glyph(FolderOpenIcon)
export const Wrench = glyph(WrenchIcon)
export const Check = glyph(CheckIcon)
export const ChevronLeft = glyph(CaretLeftIcon)
export const ChevronRight = glyph(CaretRightIcon)
export const ExternalLink = glyph(ArrowSquareOutIcon)
export const FileText = glyph(FileTextIcon)
export const Coffee = glyph(CoffeeIcon)

export type { IconProps }
