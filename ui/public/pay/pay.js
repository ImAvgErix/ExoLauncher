// The Discord Shop's card form (see index.html). It answers only Exo's own page, and says only: ready, whether the
// card is filled in, the id Stripe gives a card it saved, that a payment went through, or why not. Never card data.
'use strict'
;(() => {
  const EXO = ['https://app.exo-launcher.local']
  let stripe = null
  let card = null
  let complete = false
  const tell = message => window.parent.postMessage(message, EXO[0])
  // While Stripe may show the bank's check, the frame grows over the window (Exo makes it so); the field hides.
  const wide = on => { document.body.classList.toggle('is-wide', on); tell({ type: 'wide', on }) }
  const why = error => (error && error.message) || 'Stripe could not finish that.'

  async function save({ clientSecret, billing }) {
    if (!stripe || !card) return tell({ type: 'error', message: 'The card form is still loading.' })
    if (!complete) return tell({ type: 'error', message: 'Fill in the card number, date and code.' })
    const token = await stripe.createToken(card, { name: billing.name, address_line1: billing.address.line1, address_line2: billing.address.line2, address_city: billing.address.city, address_state: billing.address.state, address_zip: billing.address.postal_code, address_country: billing.address.country })
    if (token.error) return tell({ type: 'error', message: why(token.error) })
    wide(true)
    const result = await stripe.confirmCardSetup(clientSecret, { payment_method: { card: { token: token.token.id }, billing_details: billing } })
    wide(false)
    if (result.error) return tell({ type: 'error', message: why(result.error) })
    const method = result.setupIntent && result.setupIntent.payment_method
    if (typeof method !== 'string') return tell({ type: 'error', message: 'Stripe did not save the card.' })
    tell({ type: 'saved', paymentMethod: method })
  }

  // A purchase the bank must confirm: Stripe's own check (3-D Secure), shown over the window.
  async function confirmPayment({ key, clientSecret }) {
    if (!stripe) stripe = window.Stripe(key)
    wide(true)
    const found = await stripe.retrievePaymentIntent(clientSecret)
    let error = found.error
    const status = found.paymentIntent && found.paymentIntent.status
    if (!error && status !== 'succeeded' && status !== 'processing') error = (await stripe.confirmCardPayment(clientSecret)).error
    wide(false)
    tell(error ? { type: 'error', message: why(error) } : { type: 'paid' })
  }

  // Exo's field look, sent by Exo (the frame can't read Exo's styles); plain colors and a pixel size only.
  const color = value => (typeof value === 'string' && /^#[0-9a-f]{3,8}$|^rgba?\([\d\s.,%]+\)$/i.test(value) ? value : null)
  function mount(key, look) {
    if (card) return
    look = look || {}
    const text = color(look.text) || '#f2ede4', faint = color(look.faint) || '#78746c', bad = color(look.bad) || '#ff6b6b'
    const size = typeof look.size === 'string' && /^\d{1,2}px$/.test(look.size) ? look.size : '13px'
    document.getElementById('card').style.background = color(look.fill) || '#121212'
    stripe = window.Stripe(key)
    card = stripe.elements().create('card', {
      hidePostalCode: true,
      // Stripe's Link (its Autofill button) signs in through a popup of its own, which can't open in Exo.
      disableLink: true,
      style: {
        base: { color: text, iconColor: faint, fontFamily: 'Inter, system-ui, sans-serif', fontSize: size, lineHeight: '16px', '::placeholder': { color: faint } },
        invalid: { color: bad, iconColor: bad },
      },
    })
    card.mount('#card')
    card.on('change', event => { complete = event.complete; tell({ type: 'change', complete, message: event.error ? event.error.message : null }) })
    card.on('focus', () => document.getElementById('card').classList.add('is-focus'))
    card.on('blur', () => document.getElementById('card').classList.remove('is-focus'))
    card.on('ready', () => tell({ type: 'ready' }))
  }

  window.addEventListener('message', event => {
    if (!EXO.includes(event.origin) || event.source !== window.parent) return
    const message = event.data || {}
    if (typeof window.Stripe !== 'function') return tell({ type: 'error', message: "Stripe's card form didn't load. Check your connection." })
    if (message.type === 'mount' && typeof message.key === 'string') mount(message.key, message.look)
    else if (message.type === 'save' && typeof message.clientSecret === 'string') save(message).catch(error => { wide(false); tell({ type: 'error', message: why(error) }) })
    else if (message.type === 'confirmPayment' && typeof message.clientSecret === 'string') confirmPayment(message).catch(error => { wide(false); tell({ type: 'error', message: why(error) }) })
  })
  tell({ type: 'loaded' })
})()
