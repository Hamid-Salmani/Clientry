import { lazy, Suspense, useCallback, useEffect, useMemo, useRef, useState } from 'react'
import type { FormEvent } from 'react'
import { HubConnectionBuilder } from '@microsoft/signalr'
import { Elements, PaymentElement, useElements, useStripe } from '@stripe/react-stripe-js'
import { loadStripe } from '@stripe/stripe-js'
import './App.css'

const AdminCharts = lazy(() => import('./AdminCharts'))
type User = { name: string; role: 'Admin' | 'Client' }
type Project = {
  id: string
  name: string
  description: string
  status: string
  startDate: string
  dueDate?: string
  progress: number
  milestones: { id: string; title: string; isCompleted: boolean; dueDate?: string }[]
}
type Invoice = { id: string; number: string; issueDate: string; dueDate: string; status: string; total: number; currency: string }
type Metrics = {
  activeProjects: number
  clientCount: number
  revenueThisMonth: number
  unpaidInvoices: number
  overdueInvoices: number
  projectStatus: { status: string; count: number }[]
  revenueByMonth: { month: string; revenue: number }[]
  recentActivity: { action: string; metadata: string; createdAt: string }[]
}
type Comment = { id: string; body: string; createdAt: string; author: string }
type ProjectFile = { id: string; fileName: string; contentType: string; size: number; uploadedAt: string }

const apiBase = import.meta.env.VITE_API_URL ?? ''
const stripePublishableKey = import.meta.env.VITE_STRIPE_PUBLISHABLE_KEY ?? ''
const stripePromise = stripePublishableKey ? loadStripe(stripePublishableKey) : null

async function request<T>(path: string, token: string, init?: RequestInit): Promise<T> {
  const response = await fetch(`${apiBase}/api${path}`, {
    ...init,
    headers: {
      ...(init?.body instanceof FormData ? {} : { 'Content-Type': 'application/json' }),
      Authorization: `Bearer ${token}`,
      ...init?.headers,
    },
  })
  if (!response.ok) {
    const body = await response.json().catch(() => null) as { message?: string; title?: string } | null
    throw new Error(body?.message ?? body?.title ?? `Request failed (${response.status})`)
  }
  if (response.status === 204) return undefined as T
  return response.json() as Promise<T>
}

function Icon({ name }: { name: string }) {
  const paths: Record<string, string> = {
    grid: 'M3 3h7v7H3zM14 3h7v7h-7zM14 14h7v7h-7zM3 14h7v7H3z',
    layers: 'm12 3 9 5-9 5-9-5 9-5Zm-9 9 9 5 9-5M3 16l9 5 9-5',
    file: 'M14 2H6a2 2 0 0 0-2 2v16a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V8zM14 2v6h6M8 13h8M8 17h8',
    users: 'M16 21v-2a4 4 0 0 0-4-4H8a4 4 0 0 0-4 4v2m6-10a4 4 0 1 0 0-8 4 4 0 0 0 0 8Zm6-7.9a4 4 0 0 1 0 7.8M20 21v-2a4 4 0 0 0-3-3.87',
    settings: 'M12 8a4 4 0 1 0 0 8 4 4 0 0 0 0-8Zm0-5v2m0 14v2m9-9h-2M5 12H3m15.36-6.36-1.42 1.42M7.06 16.94l-1.42 1.42m12.72 0-1.42-1.42M7.06 7.06 5.64 5.64',
    search: 'm20 20-4.5-4.5M18 10.5a7.5 7.5 0 1 1-15 0 7.5 7.5 0 0 1 15 0Z',
    bell: 'M18 8a6 6 0 0 0-12 0c0 7-3 7-3 9h18c0-2-3-2-3-9m-8 12a2 2 0 0 0 4 0',
    building: 'M3 21h18M5 21V5l7-3 7 3v16M9 9h.01M9 13h.01M9 17h.01M15 9h.01M15 13h.01M15 17h.01',
    chevron: 'm7 10 5 5 5-5',
    close: 'm18 6-12 12M6 6l12 12',
    arrow: 'M5 12h14m-7-7 7 7-7 7',
    upload: 'M21 15v4a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2v-4m4-5 5-5 5 5m-5-5v12',
    logout: 'M9 21H5a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2h4m7 14 5-5-5-5m5 5H9',
  }
  return <svg className="icon" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.7" strokeLinecap="round" strokeLinejoin="round" aria-hidden="true"><path d={paths[name] ?? paths.grid} /></svg>
}

function App() {
  const [token, setToken] = useState(() => sessionStorage.getItem('clienthub-token') ?? '')
  const [user, setUser] = useState<User | null>(null)
  const [email, setEmail] = useState('clientA@demo.com')
  const [password, setPassword] = useState('DemoPass!234')
  const [loginError, setLoginError] = useState('')
  const [activePage, setActivePage] = useState('Overview')
  const [openPopover, setOpenPopover] = useState<'search' | 'notifications' | 'workspace' | null>(null)
  const [searchTerm, setSearchTerm] = useState('')
  const [notificationsSeen, setNotificationsSeen] = useState(false)
  const [workspaceUpdates, setWorkspaceUpdates] = useState(() => localStorage.getItem('clientry-workspace-updates') !== 'false')
  const [invoiceReminders, setInvoiceReminders] = useState(() => localStorage.getItem('clientry-invoice-reminders') !== 'false')
  const [creatingProject, setCreatingProject] = useState(false)
  const [projects, setProjects] = useState<Project[]>([])
  const [invoices, setInvoices] = useState<Invoice[]>([])
  const [metrics, setMetrics] = useState<Metrics | null>(null)
  const [clients, setClients] = useState<{ id: string; companyName: string; contact?: string; activeProjects: number }[]>([])
  const [selectedProject, setSelectedProject] = useState<Project | null>(null)
  const [comments, setComments] = useState<Comment[]>([])
  const [projectFiles, setProjectFiles] = useState<ProjectFile[]>([])
  const [commentText, setCommentText] = useState('')
  const [loading, setLoading] = useState(false)
  const [error, setError] = useState('')
  const workspaceRef = useRef<HTMLDivElement>(null)
  const actionsRef = useRef<HTMLDivElement>(null)
  const searchInputRef = useRef<HTMLInputElement>(null)

  const admin = user?.role === 'Admin'
  const workspaceName = admin ? 'Studio North' : 'Northstar Studio'
  const navigateTo = (page: string) => {
    setActivePage(page)
    setSelectedProject(null)
    setOpenPopover(null)
  }

  useEffect(() => {
    if (!openPopover) return
    const closeOnOutsideClick = (event: PointerEvent) => {
      const target = event.target as Node
      if (!workspaceRef.current?.contains(target) && !actionsRef.current?.contains(target)) setOpenPopover(null)
    }
    const closeOnEscape = (event: KeyboardEvent) => {
      if (event.key === 'Escape') setOpenPopover(null)
    }
    document.addEventListener('pointerdown', closeOnOutsideClick)
    document.addEventListener('keydown', closeOnEscape)
    return () => {
      document.removeEventListener('pointerdown', closeOnOutsideClick)
      document.removeEventListener('keydown', closeOnEscape)
    }
  }, [openPopover])

  useEffect(() => {
    if (openPopover === 'search') searchInputRef.current?.focus()
  }, [openPopover])
  const loadData = useCallback(async (authToken: string, currentUser: User) => {
    setLoading(true)
    setError('')
    try {
      const [projectData, invoiceData] = await Promise.all([
        request<Project[]>('/projects', authToken),
        request<Invoice[]>('/invoices', authToken),
      ])
      setProjects(projectData)
      setInvoices(invoiceData)
      if (currentUser.role === 'Admin') {
        const [dashboard, clientData] = await Promise.all([
          request<Metrics>('/admin/dashboard', authToken),
          request<typeof clients>('/admin/clients', authToken),
        ])
        setMetrics(dashboard)
        setClients(clientData)
      }
    } catch (reason) {
      setError(reason instanceof Error ? reason.message : 'Unable to load your workspace.')
    } finally {
      setLoading(false)
    }
  }, [])

  useEffect(() => {
    if (!token) return
    request<{ name: string; role: User['role'] }>('/auth/me', token)
      .then((profile) => {
        const currentUser = { name: profile.name, role: profile.role }
        setUser(currentUser)
        void loadData(token, currentUser)
      })
      .catch(() => signOut())
  // signOut intentionally follows the stored token lifetime.
  // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [token, loadData])

  useEffect(() => {
    if (!selectedProject || !token) return
    request<Comment[]>(`/projects/${selectedProject.id}/comments`, token)
      .then(setComments)
      .catch((reason: unknown) => setError(reason instanceof Error ? reason.message : 'Unable to load comments.'))
    request<ProjectFile[]>(`/projects/${selectedProject.id}/files`, token)
      .then(setProjectFiles)
      .catch((reason: unknown) => setError(reason instanceof Error ? reason.message : 'Unable to load files.'))
  }, [selectedProject, token])

  const signOut = () => {
    sessionStorage.removeItem('clienthub-token')
    setToken('')
    setUser(null)
    setSelectedProject(null)
  }

  async function logIn(event: FormEvent) {
    event.preventDefault()
    setLoginError('')
    try {
      const result = await request<{ accessToken: string; name: string; role: User['role'] }>('/auth/login', '', {
        method: 'POST', body: JSON.stringify({ email, password }),
      })
      sessionStorage.setItem('clienthub-token', result.accessToken)
      setUser({ name: result.name, role: result.role })
      setToken(result.accessToken)
    } catch (reason) {
      setLoginError(reason instanceof Error ? reason.message : 'Sign in failed.')
    }
  }

  async function addComment(event: FormEvent) {
    event.preventDefault()
    if (!selectedProject || !commentText.trim()) return
    try {
      const saved = await request<{ id: string }>(`/projects/${selectedProject.id}/comments`, token, {
        method: 'POST', body: JSON.stringify({ body: commentText }),
      })
      setComments((existing) => [...existing, {
        id: saved.id, body: commentText, createdAt: new Date().toISOString(), author: user?.name ?? 'You',
      }])
      setCommentText('')
    } catch (reason) {
      setError(reason instanceof Error ? reason.message : 'Unable to post your update.')
    }
  }

  async function uploadFile(file: File) {
    if (!selectedProject) return
    try {
      const data = new FormData()
      data.append('file', file)
      await request(`/projects/${selectedProject.id}/files`, token, { method: 'POST', body: data })
      setProjectFiles(await request<ProjectFile[]>(`/projects/${selectedProject.id}/files`, token))
    } catch (reason) {
      setError(reason instanceof Error ? reason.message : 'Unable to upload this file.')
    }
  }

  async function downloadFile(file: ProjectFile) {
    if (!selectedProject) return
    try {
      const response = await fetch(`${apiBase}/api/projects/${selectedProject.id}/files/${file.id}`, {
        headers: { Authorization: `Bearer ${token}` },
      })
      if (!response.ok) throw new Error(`Could not download file (${response.status}).`)
      const url = URL.createObjectURL(await response.blob())
      const anchor = document.createElement('a'); anchor.href = url; anchor.download = file.fileName; anchor.click()
      URL.revokeObjectURL(url)
    } catch (reason) {
      setError(reason instanceof Error ? reason.message : 'Unable to download this file.')
    }
  }

  const greeting = useMemo(() => user?.name.split(' ')[0] ?? 'there', [user])
  if (!token || !user) return <Login email={email} setEmail={setEmail} password={password} setPassword={setPassword} onSubmit={logIn} error={loginError} />

  return (
    <div className="app-shell">
      <aside className="sidebar">
        <div className="brand"><div className="brand-mark">c</div><span>clientry</span></div>
        <div className="workspace-container" ref={workspaceRef}>
          <button className={`workspace-switch ${openPopover === 'workspace' ? 'open' : ''}`} aria-expanded={openPopover === 'workspace'} onClick={() => setOpenPopover(openPopover === 'workspace' ? null : 'workspace')}>
            <span className="workspace-avatar"><Icon name="building" /></span>
            <span className="workspace-meta"><strong>{workspaceName}</strong><span>{admin ? 'Agency workspace' : 'Client workspace'}</span></span>
            <Icon name="chevron" />
          </button>
          {openPopover === 'workspace' && <div className="popover workspace-popover">
            <div className="popover-heading"><span className="popover-icon"><Icon name="building" /></span><div><strong>{workspaceName}</strong><span>Current workspace</span></div></div>
            <p>{admin ? 'Manage your studio and client relationships.' : 'Your projects, files, and invoices in one place.'}</p>
            {admin && <button className="popover-link" onClick={() => navigateTo('Clients')}>View clients <Icon name="arrow" /></button>}
            <button className="popover-link" onClick={() => navigateTo('Settings')}>Workspace settings <Icon name="arrow" /></button>
          </div>}
        </div>
        <div className="nav-label">WORKSPACE</div>
        <nav className="nav-list" aria-label="Workspace navigation">
          {(admin ? ['Overview', 'Projects', 'Invoices', 'Clients'] : ['Overview', 'Projects', 'Invoices']).map((item) => (
            <button key={item} className={`nav-item ${activePage === item ? 'active' : ''}`} onClick={() => navigateTo(item)}>
              <Icon name={item === 'Overview' ? 'grid' : item === 'Projects' ? 'layers' : item === 'Invoices' ? 'file' : 'users'} />{item}
              {item === 'Invoices' && invoices.some((invoice) => invoice.status === 'Sent' || invoice.status === 'Overdue') && <span className="nav-count">{invoices.filter((invoice) => invoice.status === 'Sent' || invoice.status === 'Overdue').length}</span>}
            </button>
          ))}
        </nav>
        <div className="sidebar-bottom">
          <button className={`nav-item muted ${activePage === 'Settings' ? 'active' : ''}`} onClick={() => navigateTo('Settings')}><Icon name="settings" />Settings</button>
          <div className="profile">
            <button className="profile-account" onClick={() => navigateTo('Account')} aria-label={`Open ${user.name}'s account`}>
              <span className="profile-avatar">{user.name.split(' ').map((part) => part[0]).join('').slice(0, 2)}</span>
              <span className="profile-meta"><strong>{user.name}</strong><span>{user.role}</span></span>
            </button>
            <button className="icon-button" aria-label="Sign out" onClick={signOut}><Icon name="logout" /></button>
          </div>
        </div>
      </aside>

      <main className="main-panel">
        <header className="topbar">
          <div className="breadcrumbs"><span>Workspace</span><span className="crumb-separator">/</span><strong>{selectedProject?.name ?? activePage}</strong></div>
          <div className="topbar-actions" ref={actionsRef}>
            <button className="icon-button search-button" aria-label="Search workspace" aria-expanded={openPopover === 'search'} onClick={() => { setSearchTerm(''); setOpenPopover(openPopover === 'search' ? null : 'search') }}><Icon name="search" /></button>
            <button className="icon-button notification-button" aria-label="Notifications" aria-expanded={openPopover === 'notifications'} onClick={() => { setOpenPopover(openPopover === 'notifications' ? null : 'notifications'); setNotificationsSeen(true) }}><Icon name="bell" />{!notificationsSeen && <i />}</button>
            <div className="topbar-divider" />
            <button className="topbar-avatar" aria-label={`Open ${user.name}'s account`} onClick={() => navigateTo('Account')}>{user.name.split(' ').map((part) => part[0]).join('').slice(0, 2)}</button>
            {openPopover === 'search' && <div className="popover search-popover" role="dialog" aria-label="Search workspace">
              <label className="search-field"><Icon name="search" /><input ref={searchInputRef} value={searchTerm} onChange={(event) => setSearchTerm(event.target.value)} onKeyDown={(event) => { if (event.key === 'Escape') setOpenPopover(null) }} placeholder="Search projects, invoices, clients..." /><button className="icon-button" onClick={() => setOpenPopover(null)} aria-label="Close search"><Icon name="close" /></button></label>
              <SearchResults term={searchTerm} projects={projects} invoices={invoices} clients={admin ? clients : []} onOpenProject={(project) => { setSelectedProject(project); setOpenPopover(null) }} onOpenInvoices={() => navigateTo('Invoices')} onOpenClients={() => navigateTo('Clients')} />
            </div>}
            {openPopover === 'notifications' && <NotificationsPopover admin={admin} projects={projects} invoices={invoices} activity={metrics?.recentActivity ?? []} workspaceUpdates={workspaceUpdates} invoiceReminders={invoiceReminders} onOpenProject={(project) => { setSelectedProject(project); setOpenPopover(null) }} onOpenInvoices={() => navigateTo('Invoices')} />}
          </div>
        </header>

        <div className="content">
          {error && <div className="alert" role="alert">{error}<button onClick={() => setError('')} aria-label="Dismiss error">×</button></div>}
          {loading && <div className="loading-bar" aria-label="Loading workspace" />}
          {selectedProject ? (
            <ProjectDetail project={selectedProject} comments={comments} setComments={setComments} files={projectFiles} token={token} commentText={commentText} setCommentText={setCommentText} onSubmit={addComment} onUpload={uploadFile} onDownload={downloadFile} onBack={() => setSelectedProject(null)} />
          ) : activePage === 'Overview' ? (
            <Overview greeting={greeting} admin={admin} projects={projects} invoices={invoices} metrics={metrics} onOpenProject={setSelectedProject} onOpenProjects={() => navigateTo('Projects')} onOpenInvoices={() => navigateTo('Invoices')} onCreateProject={() => setCreatingProject(true)} />
          ) : activePage === 'Projects' ? (
            <Projects projects={projects} onOpenProject={setSelectedProject} />
          ) : activePage === 'Invoices' ? (
            <Invoices invoices={invoices} token={token} />
          ) : activePage === 'Settings' ? (
            <Settings workspaceName={workspaceName} workspaceUpdates={workspaceUpdates} invoiceReminders={invoiceReminders} onWorkspaceUpdatesChange={(enabled) => { setWorkspaceUpdates(enabled); localStorage.setItem('clientry-workspace-updates', String(enabled)) }} onInvoiceRemindersChange={(enabled) => { setInvoiceReminders(enabled); localStorage.setItem('clientry-invoice-reminders', String(enabled)) }} />
          ) : activePage === 'Account' ? (
            <Account user={user} workspaceName={workspaceName} onSignOut={signOut} />
          ) : (
            <Clients clients={clients} />
          )}
        </div>
      </main>
      {creatingProject && <ProjectCreateModal clients={clients} token={token} onClose={() => setCreatingProject(false)} onCreated={() => { setCreatingProject(false); if (user) void loadData(token, user) }} />}
    </div>
  )
}

function Login({ email, setEmail, password, setPassword, onSubmit, error }: {
  email: string; setEmail: (value: string) => void; password: string; setPassword: (value: string) => void
  onSubmit: (event: FormEvent) => void; error: string
}) {
  return <main className="login-page">
    <section className="login-card">
      <div className="brand login-brand"><div className="brand-mark">c</div><span>clientry</span></div>
      <div className="login-copy"><div className="eyebrow">YOUR WORK, IN ONE PLACE</div><h1>Welcome back.</h1><p>Sign in to pick up where you left off.</p></div>
      <form className="login-form" onSubmit={onSubmit}>
        <label htmlFor="email">Email address</label><input id="email" type="email" autoComplete="username" value={email} onChange={(event) => setEmail(event.target.value)} required />
        <div className="password-label"><label htmlFor="password">Password</label><span>Demo: DemoPass!234</span></div>
        <input id="password" type="password" autoComplete="current-password" value={password} onChange={(event) => setPassword(event.target.value)} required />
        {error && <div className="login-error" role="alert">{error}</div>}
        <button className="primary-button login-submit" type="submit">Sign in <Icon name="arrow" /></button>
      </form>
      <div className="login-demo"><span>Try a demo workspace</span><div><button onClick={() => { setEmail('clientA@demo.com'); setPassword('DemoPass!234') }}>Client portal</button><button onClick={() => { setEmail('admin@demo.com'); setPassword('DemoPass!234') }}>Agency admin</button></div></div>
      <p className="login-footer">Secure client collaboration, made simple.</p>
    </section>
    <aside className="login-art" aria-hidden="true"><div className="art-glow" /><div className="art-orbit orbit-one" /><div className="art-orbit orbit-two" /><div className="art-card"><div className="art-card-head"><span className="art-dot" /><span>PROJECT OVERVIEW</span><span>•••</span></div><strong>Brand identity refresh</strong><small>Northstar Studio · Due Dec 12</small><div className="art-progress"><i /></div><div className="art-progress-label"><span>Project progress</span><b>50%</b></div><div className="art-checks"><span>✓ &nbsp; Discovery & strategy</span><span>✓ &nbsp; Visual directions</span><span className="check-pending">◌ &nbsp; Identity system</span></div></div><div className="art-note"><span className="note-avatar">A</span><div><strong>Alex Morgan</strong><p>The first visual directions are ready for review.</p></div><span className="note-time">now</span></div><div className="art-stamp">Thoughtfully<br />connected.</div></aside>
  </main>
}

function SearchResults({ term, projects, invoices, clients, onOpenProject, onOpenInvoices, onOpenClients }: {
  term: string
  projects: Project[]
  invoices: Invoice[]
  clients: { id: string; companyName: string; contact?: string; activeProjects: number }[]
  onOpenProject: (project: Project) => void
  onOpenInvoices: () => void
  onOpenClients: () => void
}) {
  const query = term.trim().toLowerCase()
  if (!query) return <p className="popover-hint">Search by project, invoice number, or client name.</p>
  const results = [
    ...projects.filter((project) => `${project.name} ${project.description} ${project.status}`.toLowerCase().includes(query)).map((project) => ({
      key: `project-${project.id}`, title: project.name, detail: `Project · ${project.status}`, action: () => onOpenProject(project),
    })),
    ...invoices.filter((invoice) => `${invoice.number} ${invoice.status}`.toLowerCase().includes(query)).map((invoice) => ({
      key: `invoice-${invoice.id}`, title: invoice.number, detail: `Invoice · ${invoice.status}`, action: onOpenInvoices,
    })),
    ...clients.filter((client) => `${client.companyName} ${client.contact ?? ''}`.toLowerCase().includes(query)).map((client) => ({
      key: `client-${client.id}`, title: client.companyName, detail: 'Client', action: onOpenClients,
    })),
  ].slice(0, 7)
  return results.length
    ? <div className="search-results">{results.map((result) => <button key={result.key} className="search-result" onClick={result.action}><span><strong>{result.title}</strong><small>{result.detail}</small></span><Icon name="arrow" /></button>)}</div>
    : <p className="popover-hint">No matching projects, invoices, or clients.</p>
}

function NotificationsPopover({ admin, projects, invoices, activity, workspaceUpdates, invoiceReminders, onOpenProject, onOpenInvoices }: {
  admin: boolean
  projects: Project[]
  invoices: Invoice[]
  activity: Metrics['recentActivity']
  workspaceUpdates: boolean
  invoiceReminders: boolean
  onOpenProject: (project: Project) => void
  onOpenInvoices: () => void
}) {
  const entries: { key: string; title: string; detail: string; when: string; action?: () => void }[] = admin
    ? activity.filter((item) => /invoice|payment/i.test(`${item.action} ${item.metadata}`) ? invoiceReminders : workspaceUpdates).slice(0, 5).map((item, index) => ({
      key: `activity-${item.createdAt}-${index}`, title: item.action, detail: item.metadata,
      when: relativeDate(item.createdAt),
    }))
    : [
      ...(workspaceUpdates ? projects.slice(0, 3).map((project) => ({
        key: `project-${project.id}`, title: project.name,
        detail: `${project.status.replace(/([A-Z])/g, ' $1').trim()} · ${project.progress}% complete`,
        when: project.dueDate ? `Due ${new Date(project.dueDate).toLocaleDateString('en', { month: 'short', day: 'numeric' })}` : 'Project update',
        action: () => onOpenProject(project),
      })) : []),
      ...(invoiceReminders ? invoices.filter((invoice) => invoice.status === 'Sent' || invoice.status === 'Overdue').slice(0, 2).map((invoice) => ({
        key: `invoice-${invoice.id}`, title: `${invoice.number} needs attention`,
        detail: `${invoice.status} · ${money(invoice.total)}`,
        when: `Due ${new Date(invoice.dueDate).toLocaleDateString('en', { month: 'short', day: 'numeric' })}`,
        action: onOpenInvoices,
      })) : []),
    ].slice(0, 5)
  return <div className="popover notifications-popover" role="dialog" aria-label="Workspace notifications">
    <div className="popover-title"><div><strong>Notifications</strong><span>{admin ? 'Recent studio activity' : 'Project and billing updates'}</span></div><span className="notification-count">{entries.length}</span></div>
    {entries.length ? <div className="notification-list">{entries.map((entry) => {
      const content = <><span className="notification-mark"><Icon name="bell" /></span><span className="notification-copy"><strong>{entry.title}</strong><span>{entry.detail}</span><small>{entry.when}</small></span></>
      return entry.action
        ? <button key={entry.key} className="notification-item" onClick={entry.action}>{content}</button>
        : <div key={entry.key} className="notification-item">{content}</div>
    })}</div> : <p className="popover-hint">You’re all caught up. New workspace updates will appear here.</p>}
    {!admin && invoices.some((invoice) => invoice.status === 'Sent' || invoice.status === 'Overdue') && <button className="popover-link" onClick={onOpenInvoices}>Review invoices <Icon name="arrow" /></button>}
  </div>
}

function Settings({ workspaceName, workspaceUpdates, invoiceReminders, onWorkspaceUpdatesChange, onInvoiceRemindersChange }: {
  workspaceName: string
  workspaceUpdates: boolean
  invoiceReminders: boolean
  onWorkspaceUpdatesChange: (enabled: boolean) => void
  onInvoiceRemindersChange: (enabled: boolean) => void
}) {
  return <div className="page-enter">
    <div className="page-heading"><div><div className="eyebrow">PREFERENCES</div><h1>Settings<span className="heading-period">.</span></h1><p>Choose how you’d like to keep up with {workspaceName}.</p></div></div>
    <section className="panel settings-panel">
      <div className="panel-heading"><div><h2>Notifications</h2><p>Preferences are saved in this browser.</p></div><span className="settings-badge">PERSONAL</span></div>
      <PreferenceToggle title="Workspace updates" description="Show project and activity updates in your notification menu." checked={workspaceUpdates} onChange={onWorkspaceUpdatesChange} />
      <PreferenceToggle title="Invoice reminders" description="Keep upcoming and overdue invoices on your radar." checked={invoiceReminders} onChange={onInvoiceRemindersChange} />
    </section>
    <section className="panel settings-workspace"><span className="workspace-avatar"><Icon name="building" /></span><div><strong>{workspaceName}</strong><span>Current workspace</span></div></section>
  </div>
}

function PreferenceToggle({ title, description, checked, onChange }: {
  title: string
  description: string
  checked: boolean
  onChange: (enabled: boolean) => void
}) {
  return <label className="preference-row"><span><strong>{title}</strong><small>{description}</small></span><input type="checkbox" checked={checked} onChange={(event) => onChange(event.target.checked)} /><span className="toggle-track" aria-hidden="true" /></label>
}

function Account({ user, workspaceName, onSignOut }: { user: User; workspaceName: string; onSignOut: () => void }) {
  return <div className="page-enter">
    <div className="page-heading"><div><div className="eyebrow">YOUR PROFILE</div><h1>Account<span className="heading-period">.</span></h1><p>Your account and workspace details.</p></div></div>
    <section className="panel account-panel">
      <div className="account-identity"><span className="account-avatar">{user.name.split(' ').map((part) => part[0]).join('').slice(0, 2)}</span><div><h2>{user.name}</h2><p>{user.role}</p></div></div>
      <div className="account-detail"><span>Account name</span><strong>{user.name}</strong></div>
      <div className="account-detail"><span>Role</span><strong>{user.role === 'Admin' ? 'Agency admin' : 'Client'}</strong></div>
      <div className="account-detail"><span>Workspace</span><strong>{workspaceName}</strong></div>
      <button className="cancel-button account-signout" onClick={onSignOut}><Icon name="logout" /> Sign out</button>
    </section>
  </div>
}

function Overview({ greeting, admin, projects, invoices, metrics, onOpenProject, onOpenProjects, onOpenInvoices, onCreateProject }: {
  greeting: string; admin: boolean; projects: Project[]; invoices: Invoice[]; metrics: Metrics | null
  onOpenProject: (project: Project) => void; onOpenProjects: () => void; onOpenInvoices: () => void; onCreateProject: () => void
}) {
  const active = projects.filter((project) => project.status !== 'Archived')
  const outstanding = invoices.filter((invoice) => invoice.status === 'Sent' || invoice.status === 'Overdue')
  return <div className="page-enter">
    <div className="page-heading"><div><div className="eyebrow">YOUR WORKSPACE</div><h1>Good morning, {greeting}<span className="heading-period">.</span></h1><p>{admin ? 'Here’s what’s happening across your studio.' : 'Here’s the latest across your projects.'}</p></div>{admin && <button className="primary-button" onClick={onCreateProject}><span>＋</span> New project</button>}</div>
    <div className="stats-grid">
      <StatCard label={admin ? 'Active projects' : 'Your projects'} value={admin ? metrics?.activeProjects : active.length} hint={admin ? 'Across all clients' : 'Currently in progress'} accent="lavender" />
      <StatCard label={admin ? 'Revenue this month' : 'Open invoices'} value={admin ? money(metrics?.revenueThisMonth ?? 0) : outstanding.length} hint={admin ? 'From paid invoices' : outstanding.length ? 'One needs your attention' : 'You’re all caught up'} accent="mint" />
      <StatCard label={admin ? 'Unpaid invoices' : 'Next milestone'} value={admin ? metrics?.unpaidInvoices : (active[0]?.milestones.filter((item) => !item.isCompleted).length ?? 0)} hint={admin ? `${metrics?.overdueInvoices ?? 0} overdue` : 'Coming up in your projects'} accent="peach" />
      <StatCard label={admin ? 'Clients' : 'Completed milestones'} value={admin ? metrics?.clientCount : active.reduce((sum, project) => sum + project.milestones.filter((item) => item.isCompleted).length, 0)} hint={admin ? 'Active relationships' : 'A little progress adds up'} accent="blue" />
    </div>
    <div className="overview-grid">
      <section className="panel projects-panel">
        <div className="panel-heading"><div><h2>{admin ? 'Project pulse' : 'Your projects'}</h2><p>{admin ? 'A quick look at work in motion.' : 'Everything you’re working on, at a glance.'}</p></div><button className="text-button" onClick={onOpenProjects}>View all <Icon name="arrow" /></button></div>
        {active.length ? <div className="project-list">{active.slice(0, 4).map((project) => <ProjectRow key={project.id} project={project} onClick={() => onOpenProject(project)} />)}</div> : <EmptyState title="Nothing in progress just yet" text="Your active projects will appear here." />}
      </section>
      <section className="panel invoice-panel">
        <div className="panel-heading"><div><h2>{admin ? 'Recent invoices' : 'Invoices'}</h2><p>{outstanding.length ? `${outstanding.length} awaiting attention` : 'No payments due right now.'}</p></div><button className="icon-button subtle" onClick={onOpenInvoices} aria-label="View all invoices"><Icon name="arrow" /></button></div>
        {invoices.length ? <div className="invoice-list">{invoices.slice(0, 4).map((invoice) => <InvoiceRow key={invoice.id} invoice={invoice} />)}</div> : <EmptyState title="No invoices yet" text="Your invoices will show up here." />}
        <button className="invoice-link" onClick={onOpenInvoices}>See all invoices <Icon name="arrow" /></button>
      </section>
      {admin && metrics?.recentActivity && <section className="panel activity-panel"><div className="panel-heading"><div><h2>Recent activity</h2><p>The latest updates from your workspace.</p></div><span className="activity-live"><i /> Live</span></div><div className="activity-list">{metrics.recentActivity.map((item, index) => <div className="activity-item" key={`${item.createdAt}-${index}`}><div className="activity-mark">{index === 0 ? '✦' : '↗'}</div><div><strong>{item.action}</strong><span>{item.metadata}</span></div><time>{relativeDate(item.createdAt)}</time></div>)}</div></section>}
    </div>
    {admin && metrics && <Suspense fallback={<div className="chart-loading" aria-label="Loading charts" />}><AdminCharts metrics={metrics} /></Suspense>}
    <footer className="page-footer"><span>Made for good work.</span><span>Need a hand? <a href="mailto:hello@clientry.app">Talk to your team</a></span></footer>
  </div>
}

function StatCard({ label, value, hint, accent }: { label: string; value?: string | number; hint: string; accent: string }) {
  return <article className={`stat-card stat-${accent}`}><div className="stat-top"><span>{label}</span><span className="stat-mark">↗</span></div><strong>{value ?? '—'}</strong><small>{hint}</small></article>
}

function ProjectRow({ project, onClick }: { project: Project; onClick: () => void }) {
  return <button className="project-row" onClick={onClick}><div className="project-icon"><Icon name="layers" /></div><div className="project-info"><strong>{project.name}</strong><span>{project.description}</span></div><div className="project-progress"><div className="progress-caption"><span>{project.progress}% complete</span><b>{project.dueDate ? new Date(project.dueDate).toLocaleDateString('en', { month: 'short', day: 'numeric' }) : 'No due date'}</b></div><div className="progress-track"><i style={{ width: `${project.progress}%` }} /></div></div><span className={`status-pill status-${project.status.toLowerCase()}`}>{project.status.replace(/([A-Z])/g, ' $1').trim()}</span><Icon name="arrow" /></button>
}

function Projects({ projects, onOpenProject }: { projects: Project[]; onOpenProject: (project: Project) => void }) {
  return <div className="page-enter"><div className="page-heading"><div><div className="eyebrow">YOUR WORKSPACE</div><h1>Projects<span className="heading-period">.</span></h1><p>All the moving pieces, in one clear view.</p></div></div><section className="panel full-panel">{projects.length ? projects.map((project) => <ProjectRow key={project.id} project={project} onClick={() => onOpenProject(project)} />) : <EmptyState title="No projects yet" text="Your team will add a project here when work begins." />}</section></div>
}

function Invoices({ invoices, token }: { invoices: Invoice[]; token: string }) {
  const [busy, setBusy] = useState('')
  const [error, setError] = useState('')
  const [paymentInvoice, setPaymentInvoice] = useState<Invoice | null>(null)
  const [clientSecret, setClientSecret] = useState('')
  const [paymentComplete, setPaymentComplete] = useState(false)
  async function download(invoice: Invoice) {
    setBusy(invoice.id); setError('')
    try {
      const response = await fetch(`${apiBase}/api/invoices/${invoice.id}/pdf`, { headers: { Authorization: `Bearer ${token}` } })
      if (!response.ok) throw new Error(`Could not download invoice (${response.status}).`)
      const url = URL.createObjectURL(await response.blob())
      const anchor = document.createElement('a'); anchor.href = url; anchor.download = `${invoice.number}.pdf`; anchor.click()
      URL.revokeObjectURL(url)
    } catch (reason) { setError(reason instanceof Error ? reason.message : 'Download failed.') }
    finally { setBusy('') }
  }
  async function beginPayment(invoice: Invoice) {
    setError(''); setPaymentComplete(false); setBusy(invoice.id)
    try {
      const result = await request<{ clientSecret: string }>(`/invoices/${invoice.id}/payment-intent`, token, { method: 'POST' })
      setPaymentInvoice(invoice)
      setClientSecret(result.clientSecret)
    } catch (reason) { setError(reason instanceof Error ? reason.message : 'Unable to start payment.') }
    finally { setBusy('') }
  }
  return <div className="page-enter">
    <div className="page-heading"><div><div className="eyebrow">BILLING</div><h1>Invoices<span className="heading-period">.</span></h1><p>Everything related to your billing, all together.</p></div><span className="invoice-total">{invoices.length} total</span></div>
    {error && <div className="alert" role="alert">{error}</div>}
    <section className="panel full-panel">
      <div className="invoice-table-head"><span>INVOICE</span><span>ISSUED</span><span>DUE DATE</span><span>STATUS</span><span>AMOUNT</span><span /></div>
      {invoices.length ? invoices.map((invoice) => <div className="invoice-table-row" key={invoice.id}>
        <div className="invoice-number"><span className="invoice-file"><Icon name="file" /></span><strong>{invoice.number}</strong></div>
        <span>{new Date(invoice.issueDate).toLocaleDateString('en', { month: 'short', day: 'numeric', year: 'numeric' })}</span>
        <span>{new Date(invoice.dueDate).toLocaleDateString('en', { month: 'short', day: 'numeric', year: 'numeric' })}</span>
        <span className={`status-pill status-${invoice.status.toLowerCase()}`}>{invoice.status}</span>
        <strong>{new Intl.NumberFormat('en-US', { style: 'currency', currency: invoice.currency }).format(invoice.total)}</strong>
        <div className="invoice-actions">
          {(invoice.status === 'Sent' || invoice.status === 'Overdue') && stripePromise && <button className="pay-button" onClick={() => void beginPayment(invoice)} disabled={busy === invoice.id}>{busy === invoice.id ? '...' : 'Pay'}</button>}
          <button className="download-button" onClick={() => void download(invoice)} disabled={busy === invoice.id}>{busy === invoice.id ? '...' : 'PDF'} <Icon name="arrow" /></button>
        </div>
      </div>) : <EmptyState title="No invoices yet" text="Any new invoice will be easy to find here." />}
    </section>
    {!stripePromise && invoices.some((invoice) => invoice.status === 'Sent' || invoice.status === 'Overdue') && <p className="payment-note">Online payments are not configured for this demo.</p>}
    {paymentInvoice && clientSecret && stripePromise && <div className="modal-backdrop" role="presentation" onMouseDown={(event) => { if (event.target === event.currentTarget) { setPaymentInvoice(null); setClientSecret('') } }}>
      <section className="payment-modal" role="dialog" aria-modal="true" aria-labelledby="payment-title">
        <button className="modal-close" onClick={() => { setPaymentInvoice(null); setClientSecret('') }} aria-label="Close payment">×</button>
        <div className="eyebrow">SECURE CHECKOUT</div><h2 id="payment-title">Pay invoice {paymentInvoice.number}</h2>
        <p className="payment-description">{new Intl.NumberFormat('en-US', { style: 'currency', currency: paymentInvoice.currency }).format(paymentInvoice.total)}</p>
        {paymentComplete ? <div className="payment-success" role="status">Payment submitted. The invoice will update when the verified payment webhook arrives.</div> :
          <Elements stripe={stripePromise} options={{ clientSecret }}><PaymentForm onComplete={() => setPaymentComplete(true)} /></Elements>}
      </section>
    </div>}
  </div>
}

function PaymentForm({ onComplete }: { onComplete: () => void }) {
  const stripe = useStripe()
  const elements = useElements()
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState('')
  async function submit(event: FormEvent) {
    event.preventDefault()
    if (!stripe || !elements) return
    setBusy(true); setError('')
    const result = await stripe.confirmPayment({ elements, redirect: 'if_required' })
    if (result.error) setError(result.error.message ?? 'Payment could not be completed.')
    else onComplete()
    setBusy(false)
  }
  return <form className="payment-form" onSubmit={submit}><PaymentElement />{error && <p className="payment-error" role="alert">{error}</p>}<button className="primary-button" disabled={!stripe || busy}>{busy ? 'Processing...' : 'Confirm payment'}</button></form>
}

function Clients({ clients }: { clients: { id: string; companyName: string; contact?: string; activeProjects: number }[] }) {
  return <div className="page-enter"><div className="page-heading"><div><div className="eyebrow">RELATIONSHIPS</div><h1>Clients<span className="heading-period">.</span></h1><p>Good work starts with good relationships.</p></div></div><section className="panel full-panel">{clients.length ? clients.map((client) => <div className="client-row" key={client.id}><div className="client-avatar">{client.companyName[0]}</div><div className="client-details"><strong>{client.companyName}</strong><span>{client.contact ?? 'No active contact'}</span></div><span className="client-project-count">{client.activeProjects} active {client.activeProjects === 1 ? 'project' : 'projects'}</span></div>) : <EmptyState title="No clients to show" text="Your client relationships will appear here." />}</section></div>
}

function ProjectCreateModal({ clients, token, onClose, onCreated }: {
  clients: { id: string; companyName: string }[]; token: string; onClose: () => void; onCreated: () => void
}) {
  const [clientId, setClientId] = useState(clients[0]?.id ?? '')
  const [name, setName] = useState('')
  const [description, setDescription] = useState('')
  const [startDate, setStartDate] = useState(() => new Date().toISOString().slice(0, 10))
  const [dueDate, setDueDate] = useState('')
  const [error, setError] = useState('')
  const [busy, setBusy] = useState(false)
  async function submit(event: FormEvent) {
    event.preventDefault()
    setBusy(true); setError('')
    try {
      await request('/projects', token, {
        method: 'POST',
        body: JSON.stringify({ clientId, name, description, startDate, dueDate: dueDate || null }),
      })
      onCreated()
    } catch (reason) {
      setError(reason instanceof Error ? reason.message : 'Unable to create the project.')
    } finally { setBusy(false) }
  }
  return <div className="modal-backdrop" role="presentation" onMouseDown={(event) => { if (event.target === event.currentTarget) onClose() }}>
    <section className="payment-modal project-modal" role="dialog" aria-modal="true" aria-labelledby="create-project-title">
      <button className="modal-close" onClick={onClose} aria-label="Close project form">×</button>
      <div className="eyebrow">WORKSPACE</div><h2 id="create-project-title">Create a project</h2><p className="payment-description">Give your client a clear view of what’s in motion.</p>
      <form className="create-project-form" onSubmit={submit}>
        <label>Client<select value={clientId} onChange={(event) => setClientId(event.target.value)} required>{clients.map((client) => <option value={client.id} key={client.id}>{client.companyName}</option>)}</select></label>
        <label>Project name<input value={name} onChange={(event) => setName(event.target.value)} maxLength={160} required /></label>
        <label>Description<textarea value={description} onChange={(event) => setDescription(event.target.value)} maxLength={4000} required /></label>
        <div className="date-fields"><label>Start date<input type="date" value={startDate} onChange={(event) => setStartDate(event.target.value)} required /></label><label>Due date<input type="date" value={dueDate} onChange={(event) => setDueDate(event.target.value)} /></label></div>
        {error && <p className="payment-error" role="alert">{error}</p>}
        <div className="modal-actions"><button type="button" className="cancel-button" onClick={onClose}>Cancel</button><button type="submit" className="primary-button" disabled={busy || clients.length === 0}>{busy ? 'Creating...' : 'Create project'}</button></div>
      </form>
    </section>
  </div>
}

function ProjectDetail({ project, comments, setComments, files, token, commentText, setCommentText, onSubmit, onUpload, onDownload, onBack }: {
  project: Project; comments: Comment[]; setComments: React.Dispatch<React.SetStateAction<Comment[]>>
  files: ProjectFile[]; token: string; commentText: string; setCommentText: (value: string) => void
  onSubmit: (event: FormEvent) => void; onUpload: (file: File) => void; onDownload: (file: ProjectFile) => void; onBack: () => void
}) {
  const [realtimeError, setRealtimeError] = useState('')
  useEffect(() => {
    const connection = new HubConnectionBuilder()
      .withUrl(`${apiBase}/hubs/projects/${project.id}`, { accessTokenFactory: () => token })
      .withAutomaticReconnect()
      .build()
    connection.on('CommentAdded', (comment: Comment) => {
      setComments((existing) => existing.some((item) => item.id === comment.id) ? existing : [...existing, comment])
      setRealtimeError('')
    })
    connection.onreconnecting(() => setRealtimeError('Live updates reconnecting…'))
    connection.onreconnected(() => setRealtimeError(''))
    connection.start().catch(() => setRealtimeError('Live updates are unavailable. You can still post updates.'))
    return () => { void connection.stop() }
  }, [project.id, setComments, token])

  return <div className="page-enter">
    <button className="back-button" onClick={onBack}>← &nbsp; All projects</button>
    <div className="project-detail-heading"><div className="project-icon large"><Icon name="layers" /></div><div><div className="eyebrow">PROJECT OVERVIEW</div><h1>{project.name}<span className="heading-period">.</span></h1><p>{project.description}</p></div><span className={`status-pill status-${project.status.toLowerCase()}`}>{project.status.replace(/([A-Z])/g, ' $1').trim()}</span></div>
    {realtimeError && <div className="realtime-note" role="status">{realtimeError}</div>}
    <div className="detail-grid">
      <div className="detail-stack">
        <section className="panel detail-main">
          <div className="panel-heading"><div><h2>Milestones</h2><p>Your progress, step by step.</p></div><span className="completion-label">{project.progress}% complete</span></div>
          <div className="detail-progress"><i style={{ width: `${project.progress}%` }} /></div>
          <div className="milestone-list">{project.milestones.map((milestone, index) => <div className="milestone" key={milestone.id}><div className={`milestone-check ${milestone.isCompleted ? 'done' : ''}`}>{milestone.isCompleted ? '✓' : String(index + 1).padStart(2, '0')}</div><div className="milestone-copy"><strong>{milestone.title}</strong><span>{milestone.isCompleted ? 'Completed' : milestone.dueDate ? `Due ${new Date(milestone.dueDate).toLocaleDateString('en', { month: 'short', day: 'numeric' })}` : 'Up next'}</span></div>{milestone.isCompleted && <span className="done-tag">DONE</span>}</div>)}</div>
          <div className="project-dates"><span>Started <b>{new Date(project.startDate).toLocaleDateString('en', { month: 'short', day: 'numeric', year: 'numeric' })}</b></span><span>Due <b>{project.dueDate ? new Date(project.dueDate).toLocaleDateString('en', { month: 'short', day: 'numeric', year: 'numeric' }) : 'Not set'}</b></span></div>
        </section>
        <section className="panel files-panel">
          <div className="panel-heading"><div><h2>Project files</h2><p>Shared files for this project.</p></div><label className="upload-button"><Icon name="upload" /> Upload<input type="file" accept=".pdf,.png,.jpg,.jpeg,.txt" onChange={(event) => { const file = event.target.files?.[0]; if (file) onUpload(file); event.currentTarget.value = '' }} /></label></div>
          {files.length ? <div className="file-list">{files.map((file) => <div className="file-row" key={file.id}><span className="invoice-file"><Icon name="file" /></span><span className="file-meta"><strong>{file.fileName}</strong><small>{(file.size / 1024).toFixed(0)} KB · {relativeDate(file.uploadedAt)}</small></span><button className="download-button" onClick={() => onDownload(file)}>Download <Icon name="arrow" /></button></div>)}</div> : <EmptyState title="No files shared yet" text="Upload a file to keep everything in one place." />}
        </section>
      </div>
      <section className="panel comments-panel">
        <div className="panel-heading"><div><h2>Conversation</h2><p>Keep project updates in one place.</p></div><span className="conversation-count">{comments.length}</span></div>
        <div className="comments-list">{comments.length ? comments.map((comment) => <article className="comment" key={comment.id}><div className="comment-avatar">{comment.author.split(' ').map((part) => part[0]).join('').slice(0, 2)}</div><div className="comment-body"><div><strong>{comment.author}</strong><time>{relativeDate(comment.createdAt)}</time></div><p>{comment.body}</p></div></article>) : <EmptyState title="Start the conversation" text="Share a question or update with your team." />}</div>
        <form className="comment-form" onSubmit={onSubmit}><label className="sr-only" htmlFor="comment">Write an update</label><textarea id="comment" value={commentText} onChange={(event) => setCommentText(event.target.value)} placeholder="Write an update..." maxLength={5000} /><button className="primary-button" disabled={!commentText.trim()}>Send update <Icon name="arrow" /></button></form>
      </section>
    </div>
  </div>
}

function InvoiceRow({ invoice }: { invoice: Invoice }) {
  return <div className="invoice-row"><div className="invoice-icon"><Icon name="file" /></div><div className="invoice-row-info"><strong>{invoice.number}</strong><span>Due {new Date(invoice.dueDate).toLocaleDateString('en', { month: 'short', day: 'numeric' })}</span></div><div className="invoice-row-price"><strong>{money(invoice.total)}</strong><span className={`status-dot-label ${invoice.status.toLowerCase()}`}><i />{invoice.status}</span></div></div>
}

function EmptyState({ title, text }: { title: string; text: string }) {
  return <div className="empty-state"><div className="empty-symbol">✳</div><strong>{title}</strong><span>{text}</span></div>
}

function money(value: number) {
  return new Intl.NumberFormat('en-US', { style: 'currency', currency: 'USD', maximumFractionDigits: 0 }).format(value)
}

function relativeDate(value: string) {
  const elapsed = Math.max(0, Date.now() - new Date(value).getTime())
  const minutes = Math.floor(elapsed / 60000)
  if (minutes < 1) return 'Just now'
  if (minutes < 60) return `${minutes}m ago`
  const hours = Math.floor(minutes / 60)
  if (hours < 24) return `${hours}h ago`
  return new Date(value).toLocaleDateString('en', { month: 'short', day: 'numeric' })
}

export default App
