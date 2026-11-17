import { createHmac, randomUUID } from 'node:crypto'
import { mkdir } from 'node:fs/promises'
import { join, resolve } from 'node:path'
import { fileURLToPath } from 'node:url'
import { expect, request as playwrightRequest, test, type APIRequestContext } from '@playwright/test'

const apiBaseUrl = process.env.AUDIT_API_URL ?? 'http://127.0.0.1:5082'
const mailpitBaseUrl = process.env.MAILPIT_BASE_URL ?? 'http://127.0.0.1:8025'
const webhookSecret = process.env.STRIPE_WEBHOOK_SECRET ?? 'whsec_clientry_audit_local_only'
const screenshotDirectory = resolve(fileURLToPath(new URL('../..', import.meta.url)), 'docs', 'screenshots')

test('invite-to-paid-invoice client journey is isolated and uses a signed webhook', async ({ page }) => {
  const api = await playwrightRequest.newContext({ baseURL: apiBaseUrl })
  const mailpit = await playwrightRequest.newContext({ baseURL: mailpitBaseUrl })

  try {
    const adminToken = await login(api, 'admin@demo.com')
    const clientName = `E2E Studio ${randomUUID().slice(0, 8)}`
    const clientEmail = `e2e.${randomUUID().replaceAll('-', '')}@example.test`
    const createdClientResponse = await api.post('/api/admin/clients', {
      headers: bearer(adminToken),
      data: { companyName: clientName, notes: 'Automated end-to-end verification.' },
    })
    expect(createdClientResponse.status()).toBe(201)
    const clientId = (await createdClientResponse.json() as { id: string }).id

    const invitationResponse = await api.post(`/api/admin/clients/${clientId}/invitations`, {
      headers: bearer(adminToken),
      data: { email: clientEmail, fullName: 'E2E Client' },
    })
    expect(invitationResponse.status()).toBe(201)

    const invitationMessage = await waitForMessage(mailpit, clientEmail, /invited to/i)
    const message = await mailpit.get(`/api/v1/message/${invitationMessage.ID}`)
    expect(message.ok()).toBeTruthy()
    const messageBody = await message.json() as { HTML: string }
    const invitationLink = messageBody.HTML.match(/href="([^"]*\/accept-invite\/([^"]+))"/)
    expect(invitationLink).not.toBeNull()
    const invitationUrl = invitationLink![1].replaceAll('&amp;', '&')
    const invitationToken = invitationLink![2]

    await page.goto(invitationUrl)
    await expect(page.getByRole('heading', { name: 'Accept your invitation.' })).toBeVisible()
    await page.getByLabel('Create a password').fill('E2ePass!234')
    await page.getByRole('button', { name: 'Accept invitation' }).click()
    await expect(page.getByText('Needs your attention')).toBeVisible()
    const clientToken = await page.evaluate(() => sessionStorage.getItem('clienthub-token'))
    expect(clientToken).toBeTruthy()

    const reusedInvitation = await api.post(`/api/auth/invitations/${invitationToken}/accept`, {
      data: { password: 'AnotherPass!234' },
    })
    expect(reusedInvitation.status()).toBe(404)

    const clientHeaders = bearer(clientToken!)
    const today = new Date().toISOString().slice(0, 10)
    const dueDate = new Date(Date.now() + 14 * 24 * 60 * 60 * 1000).toISOString().slice(0, 10)
    const projectResponse = await api.post('/api/projects', {
      headers: bearer(adminToken),
      data: {
        clientId, name: 'E2E Brand Refresh', description: 'Project created in the automated client journey.',
        startDate: today, dueDate,
      },
    })
    expect(projectResponse.status()).toBe(201)
    const projectId = (await projectResponse.json() as { id: string }).id

    const milestoneIds: string[] = []
    for (const title of ['Discovery', 'Design', 'Delivery']) {
      const response = await api.post(`/api/projects/${projectId}/milestones`, {
        headers: bearer(adminToken),
        data: { title, dueDate },
      })
      expect(response.status()).toBe(201)
      milestoneIds.push((await response.json() as { id: string }).id)
    }

    const upload = await api.post(`/api/projects/${projectId}/files`, {
      headers: bearer(adminToken),
      multipart: {
        file: {
          name: 'e2e-brief.txt',
          mimeType: 'text/plain',
          buffer: Buffer.from('A valid UTF-8 project brief for the audit.\n'),
        },
      },
    })
    expect(upload.status()).toBe(201)
    const uploadedFileId = (await upload.json() as { id: string }).id

    const requestApproval = await api.post(
      `/api/projects/${projectId}/milestones/${milestoneIds[0]}/approval-request`,
      { headers: bearer(adminToken) },
    )
    expect(requestApproval.status()).toBe(204)
    const approvalEmail = await waitForMessage(mailpit, clientEmail, /milestone approval requested/i)
    expect(approvalEmail.Subject).toMatch(/Milestone approval requested/i)

    await page.getByRole('button', { name: /Approvals/ }).click()
    await expect(page.getByRole('heading', { name: /Approvals/ })).toBeVisible()
    await page.getByRole('button', { name: 'Approve', exact: true }).click()
    await page.getByRole('button', { name: 'Confirm approval' }).click()
    await expect(page.getByText('You’re all caught up')).toBeVisible()
    const projectAfterApproval = await api.get(`/api/projects/${projectId}`, { headers: clientHeaders })
    expect(projectAfterApproval.ok()).toBeTruthy()
    const approvedProject = await projectAfterApproval.json() as {
      milestones: { id: string; isCompleted: boolean; approvalStatus: string }[]
    }
    expect(approvedProject.milestones.find(milestone => milestone.id === milestoneIds[0]))
      .toMatchObject({ isCompleted: true, approvalStatus: 'Approved' })

    const invoiceResponse = await api.post('/api/invoices', {
      headers: bearer(adminToken),
      data: {
        clientId, projectId, dueDate, currency: 'USD',
        lines: [{ description: 'Brand refresh', quantity: 1, unitPrice: 420 }],
        discountAmount: 20, taxAmount: 15,
      },
    })
    expect(invoiceResponse.status()).toBe(201)
    const invoiceId = (await invoiceResponse.json() as { id: string }).id

    const unpaidReceipt = await api.get(`/api/invoices/${invoiceId}/receipt.pdf`, { headers: clientHeaders })
    expect(unpaidReceipt.status()).toBe(404)
    const sentInvoice = await api.post(`/api/invoices/${invoiceId}/send`, { headers: bearer(adminToken) })
    expect(sentInvoice.status()).toBe(204)
    const invoiceEmail = await waitForMessage(mailpit, clientEmail, /invoice update/i)
    expect(invoiceEmail.Subject).toMatch(/Invoice update/i)

    const paymentIntent = await api.post(`/api/invoices/${invoiceId}/payment-intent`, { headers: clientHeaders })
    expect(paymentIntent.status()).toBe(503)

    const amountMismatch = await sendSignedPaymentWebhook(api, invoiceId, `pi_wrong_${randomUUID()}`, 41499)
    expect(amountMismatch.status()).toBe(400)
    const paymentIntentId = `pi_audit_${randomUUID()}`
    const validWebhook = await sendSignedPaymentWebhook(api, invoiceId, paymentIntentId, 41500)
    expect(validWebhook.status()).toBe(200)
    const replay = await sendSignedPaymentWebhook(api, invoiceId, paymentIntentId, 41500)
    expect(replay.status()).toBe(200)

    const paidInvoiceResponse = await api.get(`/api/invoices/${invoiceId}`, { headers: clientHeaders })
    expect(paidInvoiceResponse.ok()).toBeTruthy()
    const paidInvoice = await paidInvoiceResponse.json() as {
      status: string; total: number; payments: { stripePaymentIntentId: string }[]
    }
    expect(paidInvoice.status).toBe('Paid')
    expect(paidInvoice.total).toBe(415)
    expect(paidInvoice.payments).toHaveLength(1)
    expect(paidInvoice.payments[0].stripePaymentIntentId).toBe(paymentIntentId)

    const receipt = await api.get(`/api/invoices/${invoiceId}/receipt.pdf`, { headers: clientHeaders })
    expect(receipt.status()).toBe(200)
    expect(receipt.headers()['content-type']).toContain('application/pdf')
    expect((await receipt.body()).byteLength).toBeGreaterThan(100)

    await page.goto(`/invoices/${invoiceId}`)
    await expect(page.getByRole('button', { name: 'Download receipt PDF' })).toBeVisible()
    const [download] = await Promise.all([
      page.waitForEvent('download'),
      page.getByRole('button', { name: 'Download receipt PDF' }).click(),
    ])
    expect(download.suggestedFilename()).toContain('receipt.pdf')
    await mkdir(screenshotDirectory, { recursive: true })
    await page.screenshot({
      path: join(screenshotDirectory, 'e2e-client-paid-invoice.png'),
      fullPage: true,
      animations: 'disabled',
    })

    const clientBToken = await login(api, 'clientb@demo.com')
    const clientBHeaders = bearer(clientBToken)
    for (const [path, resourceId] of [
      ['/api/projects', projectId],
      ['/api/invoices', invoiceId],
      ['/api/approvals', milestoneIds[0]],
      ['/api/requests', clientId],
    ]) {
      const response = await api.get(path, { headers: clientBHeaders })
      expect(response.ok()).toBeTruthy()
      expect(await response.text()).not.toContain(resourceId)
    }
    expect((await api.get(`/api/projects/${projectId}`, { headers: clientBHeaders })).status()).toBe(404)
    expect((await api.get(`/api/projects/${projectId}/files/${uploadedFileId}`, { headers: clientBHeaders })).status()).toBe(404)
    expect((await api.get(`/api/invoices/${invoiceId}/receipt.pdf`, { headers: clientBHeaders })).status()).toBe(404)
  } finally {
    await api.dispose()
    await mailpit.dispose()
  }
})

async function login(api: APIRequestContext, email: string) {
  const response = await api.post('/api/auth/login', { data: { email, password: 'DemoPass!234' } })
  expect(response.status()).toBe(200)
  return (await response.json() as { accessToken: string }).accessToken
}

function bearer(token: string) {
  return { Authorization: `Bearer ${token}` }
}

async function waitForMessage(
  mailpit: APIRequestContext,
  email: string,
  subject: RegExp,
) {
  for (let attempt = 0; attempt < 30; attempt++) {
    const response = await mailpit.get('/api/v1/messages')
    expect(response.ok()).toBeTruthy()
    const body = await response.json() as {
      messages: { ID: string; Subject: string; To: { Address: string }[] }[]
    }
    const match = body.messages.find(message =>
      subject.test(message.Subject) && message.To.some(recipient => recipient.Address === email),
    )
    if (match) return match
    await new Promise(resolve => setTimeout(resolve, 250))
  }
  throw new Error(`Mailpit did not receive ${subject} for ${email}.`)
}

async function sendSignedPaymentWebhook(
  api: APIRequestContext,
  invoiceId: string,
  paymentIntentId: string,
  amount: number,
) {
  const timestamp = Math.floor(Date.now() / 1000)
  const payload = JSON.stringify({
    id: `evt_audit_${randomUUID()}`,
    object: 'event',
    api_version: '2025-03-31.basil',
    created: timestamp,
    data: {
      object: {
        id: paymentIntentId,
        object: 'payment_intent',
        amount,
        amount_received: amount,
        currency: 'usd',
        metadata: { invoiceId },
      },
    },
    livemode: false,
    pending_webhooks: 1,
    request: { id: null, idempotency_key: null },
    type: 'payment_intent.succeeded',
  })
  const signature = createHmac('sha256', webhookSecret)
    .update(`${timestamp}.${payload}`)
    .digest('hex')
  return api.post('/api/payments/stripe-webhook', {
    headers: { 'Stripe-Signature': `t=${timestamp},v1=${signature}` },
    data: payload,
  })
}
