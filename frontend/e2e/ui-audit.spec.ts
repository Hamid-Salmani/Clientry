import { mkdir } from 'node:fs/promises'
import { join, resolve } from 'node:path'
import { fileURLToPath } from 'node:url'
import { expect, test } from '@playwright/test'
import type { Page } from '@playwright/test'

const screenshotDirectory = resolve(fileURLToPath(new URL('../..', import.meta.url)), 'docs', 'screenshots')
const viewportSizes = [
  { width: 360, height: 900 },
  { width: 768, height: 900 },
  { width: 1280, height: 900 },
]

async function signInAsDemo(page: Page, account: 'client' | 'admin') {
  await page.goto('/')
  await page.getByLabel('Email address').fill(account === 'client' ? 'clienta@demo.com' : 'admin@demo.com')
  await page.getByLabel('Password').fill('DemoPass!234')
  await page.getByRole('button', { name: 'Sign in' }).click()
  await expect(page.getByRole('heading', { name: /Good (morning|afternoon|evening)/ })).toBeVisible()
}

async function expectNoHorizontalOverflow(page: Page, width: number) {
  const documentWidth = await page.evaluate(() => ({
    viewport: document.documentElement.clientWidth,
    content: document.documentElement.scrollWidth,
  }))
  expect(documentWidth.content, `${width}px viewport should not scroll horizontally`).toBeLessThanOrEqual(documentWidth.viewport)
}

async function captureResponsiveThemes(page: Page, role: 'client' | 'admin') {
  await mkdir(screenshotDirectory, { recursive: true })
  for (const viewport of viewportSizes) {
    await page.setViewportSize(viewport)
    for (const theme of ['light', 'dark'] as const) {
      await page.getByRole('button', { name: 'Settings', exact: true }).click()
      await setTheme(page, theme)
      await page.getByRole('button', { name: 'Overview', exact: true }).click()
      await expect(page.getByText('Needs your attention')).toBeVisible()
      await expectNoHorizontalOverflow(page, viewport.width)
      await page.screenshot({
        path: join(screenshotDirectory, `${role}-home-${viewport.width}-${theme}.png`),
        fullPage: true,
        animations: 'disabled',
      })
    }
  }
}

async function setTheme(page: Page, theme: 'light' | 'dark') {
  const preference = page.locator('.preference-row').filter({ hasText: 'Light mode' })
  const lightMode = preference.locator('input[type="checkbox"]')
  if ((await lightMode.isChecked()) !== (theme === 'light')) await preference.click()
  await expect(lightMode).toHaveJSProperty('checked', theme === 'light')
}

test('client portal supports direct routes, refresh, and responsive light/dark views', async ({ page }) => {
  const browserErrors: string[] = []
  page.on('pageerror', error => browserErrors.push(error.message))
  page.on('console', message => {
    if (message.type() === 'error') browserErrors.push(message.text())
  })

  await signInAsDemo(page, 'client')
  await expect(page.getByText('Pending approvals')).toBeVisible()
  await expect(page.getByText('$2,400').first()).toBeVisible()

  await page.goto('/approvals')
  await expect(page.getByRole('heading', { name: /Approvals/ })).toBeVisible()
  await page.goBack()
  await expect(page.getByText('Needs your attention')).toBeVisible()

  await page.goto('/requests')
  await page.reload()
  await expect(page.getByRole('heading', { name: /Your requests/ })).toBeVisible()

  await page.goto('/')
  await captureResponsiveThemes(page, 'client')
  expect(browserErrors).toEqual([])
})

test('admin portal loads workspace settings and responsive light/dark views', async ({ page }) => {
  const browserErrors: string[] = []
  page.on('pageerror', error => browserErrors.push(error.message))
  page.on('console', message => {
    if (message.type() === 'error') browserErrors.push(message.text())
  })

  await signInAsDemo(page, 'admin')
  await expect(page.getByRole('button', { name: 'Clients', exact: true })).toBeVisible()
  await page.goto('/settings')
  await expect(page.getByRole('textbox', { name: /Workspace name/ })).toHaveValue('Clientry')
  await page.goto('/')

  await mkdir(screenshotDirectory, { recursive: true })
  for (const viewport of viewportSizes) {
    await page.setViewportSize(viewport)
    for (const theme of ['light', 'dark'] as const) {
      await page.goto('/settings')
      await setTheme(page, theme)
      await page.goto('/')
      await expect(page.getByRole('heading', { name: /Good (morning|afternoon|evening)/ })).toBeVisible()
      await expectNoHorizontalOverflow(page, viewport.width)
      await page.screenshot({
        path: join(screenshotDirectory, `admin-home-${viewport.width}-${theme}.png`),
        fullPage: true,
        animations: 'disabled',
      })
    }
  }
  expect(browserErrors).toEqual([])
})
