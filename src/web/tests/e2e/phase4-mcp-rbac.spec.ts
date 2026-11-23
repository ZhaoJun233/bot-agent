import { expect, test, type APIRequestContext, type Page } from '@playwright/test'

const ADMIN_TOKEN = 'synthetic-web-admin-token'
const READ_TOKEN = 'synthetic-web-read-token'
const adminHeaders = { Authorization: 'Bearer ' + ADMIN_TOKEN }
const readHeaders = { Authorization: 'Bearer ' + READ_TOKEN }

async function connectAdmin(page: Page): Promise<void> {
  await page.goto('/')
  const token = page.getByTestId('token-input')
  if (await token.isVisible()) {
    await token.fill(ADMIN_TOKEN)
    await page.getByTestId('connect-button').click()
  }
  await expect(page.getByTestId('overview')).toBeVisible()
}

async function cleanupPhase4State(request: APIRequestContext): Promise<void> {
  const mcpRes = await request.get('/zba/v1/mcp/servers', { headers: readHeaders })
  if (mcpRes.ok()) {
    const data = (await mcpRes.json()) as { revision: number; servers: Array<{ id: string }> }
    if (data.servers.some((s) => s.id === 'e2e-docs')) {
      await request.post('/zba/v1/mcp/servers/delete', {
        headers: adminHeaders,
        data: {
          expectedRevision: data.revision,
          idempotencyKey: `e2e-clean-mcp-${Date.now()}`,
          id: 'e2e-docs'
        }
      })
    }
  }

  const rolesRes = await request.get('/zba/v1/permissions/roles', { headers: readHeaders })
  if (rolesRes.ok()) {
    const data = (await rolesRes.json()) as { revision: number; items: unknown[] }
    if (data.items.length > 0) {
      await request.post('/zba/v1/permissions/roles/save', {
        headers: adminHeaders,
        data: {
          expectedRevision: data.revision,
          idempotencyKey: `e2e-clean-roles-${Date.now()}`,
          bindings: []
        }
      })
    }
  }
}

test('Phase 4: console MCP SSRF block & non-echo secret + cross-platform RBAC *Raw persistence', async ({ page, request }) => {
  await page.setViewportSize({ width: 1440, height: 900 })
  await cleanupPhase4State(request)

  try {
    await connectAdmin(page)
    await page.getByTestId('nav-configuration').click()
    await expect(page.getByTestId('system-configuration')).toBeVisible()

    await page.getByTestId('sysconfig-tab-agent').click()
    await expect(page.getByTestId('sysconfig-pane-agent')).toBeVisible()
    await expect(page.getByTestId('mcp-servers-card')).toBeVisible()
    await expect(page.getByTestId('access-roles-card')).toBeVisible()

    const enableAdmin = page.getByTestId('mcp-enable-admin')
    if (await enableAdmin.isVisible()) {
      await enableAdmin.click()
    }

    // 1. SSRF rejection when saving private endpoint
    await page.getByTestId('mcp-new-btn').click()
    await page.getByTestId('mcp-input-id').fill('e2e-ssrf')
    await page.getByTestId('mcp-input-endpoint').fill('http://127.0.0.1:9000/mcp')
    await page.getByTestId('mcp-input-allowed-tools').fill('echo')
    await page.getByTestId('mcp-save-btn').click()
    await expect(page.getByTestId('mcp-servers-card')).toContainText('MCP 地址未通过安全校验')

    // 2. Save valid disabled console MCP server with write-only secret, verify non-echo, then delete
    const syntheticToken = 'sk-e2e-synthetic-never-echo'
    await page.getByTestId('mcp-input-id').fill('e2e-docs')
    await page.getByTestId('mcp-input-endpoint').fill('https://mcp.example.com/mcp')
    await page.getByTestId('mcp-input-enabled').uncheck()
    await page.getByTestId('mcp-input-allowed-tools').fill('search_docs')
    await page.getByTestId('mcp-input-readonly-tools').fill('search_docs')
    await page.getByTestId('mcp-input-timeout').fill('2')
    await page.getByTestId('mcp-input-token').fill(syntheticToken)
    await page.getByTestId('mcp-save-btn').click()

    const savedRow = page.getByTestId('mcp-server-row-e2e-docs')
    await expect(savedRow).toBeVisible()
    await expect(savedRow).toContainText('已配置凭据')
    expect(await page.evaluate(() => document.body.innerText)).not.toContain(syntheticToken)

    await page.getByTestId('mcp-delete-e2e-docs').click()
    await expect(page.getByTestId('mcp-server-row-e2e-docs')).toHaveCount(0)

    // 3. RBAC: GroupManager (role=3) with empty scope rejected with 400 banner
    await page.getByTestId('rbac-add-btn').click()
    await page.getByTestId('rbac-user-0').fill('ou_e2e_mgr_10001')
    await expect(page.getByTestId('rbac-scope-warning-0')).toBeVisible()
    await page.getByTestId('rbac-save-btn').click()
    await expect(page.getByTestId('access-roles-card')).toContainText('群组管理员（GroupManager）必须绑定具体的群聊会话 Key')

    // 4. Save valid GroupManager binding, reload page, verify unmasked *Raw value preserved, then clean up
    await page.getByTestId('rbac-scope-0').fill('feishu:group:oc_e2e_1')
    await page.getByTestId('rbac-save-btn').click()
    await expect(page.getByTestId('rbac-notice')).toContainText('已保存 1 条跨平台角色绑定')

    await page.reload()
    await connectAdmin(page)
    await page.getByTestId('nav-configuration').click()
    await page.getByTestId('sysconfig-tab-agent').click()
    await expect(page.getByTestId('access-roles-card')).toBeVisible()
    const rbacEnableAdmin = page.getByTestId('rbac-enable-admin')
    if (await rbacEnableAdmin.isVisible()) {
      await rbacEnableAdmin.click()
    }
    await expect(page.getByTestId('rbac-user-0')).toHaveValue('ou_e2e_mgr_10001')
    await expect(page.getByTestId('rbac-scope-0')).toHaveValue('feishu:group:oc_e2e_1')

    await page.getByTestId('rbac-remove-0').click()
    await page.getByTestId('rbac-save-btn').click()
    await expect(page.getByTestId('rbac-notice')).toContainText('已保存 0 条跨平台角色绑定')
  } finally {
    try { await cleanupPhase4State(request) } catch {}
  }
})
