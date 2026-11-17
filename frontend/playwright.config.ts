import { defineConfig } from '@playwright/test'

const executablePath = process.env.CHROME_EXECUTABLE_PATH

export default defineConfig({
  testDir: './e2e',
  timeout: 45_000,
  expect: { timeout: 10_000 },
  reporter: 'list',
  use: {
    baseURL: process.env.E2E_BASE_URL ?? 'http://127.0.0.1:5173',
    browserName: 'chromium',
    headless: true,
    launchOptions: executablePath ? { executablePath } : {},
    trace: 'retain-on-failure',
  },
})
