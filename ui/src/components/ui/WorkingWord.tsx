/**
 * A word for work with no measure, such as a game starting: "Preparing" with its three dots taking turns, in place of a
 * bar or a spinner that would claim a progress nobody knows. Still dots under reduced motion (styles/progress.css).
 */
export function WorkingWord({ word }: { word: string }) {
  return (
    <span className="exo-working">
      {word}
      <span className="exo-working-dots" aria-hidden="true"><span>.</span><span>.</span><span>.</span></span>
    </span>
  )
}
