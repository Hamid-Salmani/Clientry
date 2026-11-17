import { lazy, Suspense, useEffect, useMemo, useRef, useState } from 'react'
import type { FormEvent } from 'react'
import { useQuery, useQueryClient } from '@tanstack/react-query'
import { useLocation, useNavigate } from 'react-router-dom'
import { HubConnectionBuilder } from '@microsoft/signalr'
import { Elements, PaymentElement, useElements, useStripe } from '@stripe/react-stripe-js'
import { loadStripe } from '@stripe/stripe-js'
import './App.css'

const AdminCharts = lazy(() => import('./AdminCharts'))
type User = { id?: string; name: string; role: 'Admin' | 'Client'; workspaceName?: string; supportEmail?: string }
type Project = {
  id: string
  name: string
  description: string
  status: string
  startDate: string
  dueDate?: string
  progress: number
  milestones: {
    id: string; title: string; isCompleted: boolean; dueDate?: string
    approvalStatus?: string; requestedAt?: string; decisionNote?: string
  }[]
}
type ApprovalItem = {
  id: string; projectId: string; clientId: string; projectName: string; title: string
  dueDate?: string; requestedAt?: string; approvalStatus: 'Pending'
}
type ProjectRequestItem = {
  id: string; clientId: string; createdById: string; title: string; description: string
  priority: 'Low' | 'Normal' | 'High'; status: 'New' | 'InReview' | 'Accepted' | 'Declined' | 'Converted' | 'Cancelled'
  adminNote?: string; convertedProjectId?: string; createdAt: string; updatedAt: string; clientName: string
  attachments: { id: string; fileName: string; contentType: string; size: number; uploadedAt: string }[]
}
type ClientOverviewData = {
  pendingApprovals: ApprovalItem[]
  unpaidInvoices: { id: string; number: string; dueDate: string; status: string; currency: string; amount: number }[]
  unreadNotificationCount: number
  recentProjectActivity: { projectId: string; projectName: string; action: string; metadata: string; createdAt: string }[]
  nextMilestones: { id: string; projectId: string; title: string; dueDate: string; projectName: string }[]
}
type Invoice = {
  id: string; clientId: string; number: string; issueDate: string; dueDate: string
  status: string; total: number; currency: string
  discountAmount?: number; taxAmount?: number
  lines: { description: string; quantity: number; unitPrice: number }[]
}
type InvoiceDetails = Invoice & {
  lineSubtotal: number; discountAmount: number; taxAmount: number
  payments: { amount: number; status: string; paidAt?: string; stripePaymentIntentId: string }[]
  statusTimeline: { status: string; at?: string }[]
}
type ClientSummary = { id: string; companyName: string; contact?: string; activeProjects: number; isActive: boolean }
type ClientDetails = {
  id: string; companyName: string; notes: string; isActive: boolean; projectCount: number
  outstandingBalance: number; lifetimeRevenue: number
  users: { id: string; fullName: string; email: string; isActive: boolean }[]
}
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
type Comment = { id: string; authorId: string; body: string; createdAt: string; author: string }
type ProjectFile = { id: string; fileName: string; contentType: string; size: number; uploadedAt: string; uploadedById: string; uploadedByRole?: string }
type NotificationItem = { id: string; type: string; message: string; isRead: boolean; createdAt: string }
type NotificationPreferences = { workspaceUpdates: boolean; invoiceReminders: boolean }
type AdminWorkspaceSettings = { name: string; supportEmail: string }

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
    check: 'm5 12 4 4L19 6',
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
  const [email, setEmail] = useState('')
  const [password, setPassword] = useState('')
  const [fullName, setFullName] = useState('')
  const [loginError, setLoginError] = useState('')
  const [isRegisterMode, setIsRegisterMode] = useState(false)
  const [openPopover, setOpenPopover] = useState<'search' | 'notifications' | 'workspace' | null>(null)
  const [searchTerm, setSearchTerm] = useState('')
  const [notificationItems, setNotificationItems] = useState<NotificationItem[]>([])
  const [unreadNotificationCount, setUnreadNotificationCount] = useState(0)
  const [notificationLoading, setNotificationLoading] = useState(false)
  const [workspaceUpdates, setWorkspaceUpdates] = useState(true)
  const [invoiceReminders, setInvoiceReminders] = useState(true)
  const [theme, setTheme] = useState<'light' | 'dark'>(() => {
    const savedTheme = localStorage.getItem('clientry-theme')
    return savedTheme === 'light' || savedTheme === 'dark'
      ? savedTheme
      : window.matchMedia('(prefers-color-scheme: dark)').matches ? 'dark' : 'light'
  })
  const [creatingProject, setCreatingProject] = useState(false)
  const [projects, setProjects] = useState<Project[]>([])
  const [invoices, setInvoices] = useState<Invoice[]>([])
  const [metrics, setMetrics] = useState<Metrics | null>(null)
  const [clients, setClients] = useState<ClientSummary[]>([])
  const [workspaceSettings, setWorkspaceSettings] = useState<AdminWorkspaceSettings>({
    name: 'Studio North',
    supportEmail: 'support@studionorth.example',
  })
  const [selectedProject, setSelectedProject] = useState<Project | null>(null)
  const [comments, setComments] = useState<Comment[]>([])
  const [projectFiles, setProjectFiles] = useState<ProjectFile[]>([])
  const [commentText, setCommentText] = useState('')
  const [error, setError] = useState('')
  const workspaceRef = useRef<HTMLDivElement>(null)
  const actionsRef = useRef<HTMLDivElement>(null)
  const searchInputRef = useRef<HTMLInputElement>(null)
  const routerNavigate = useNavigate()
  const queryClient = useQueryClient()
  const location = useLocation()
  const projectRouteId = location.pathname.match(/^\/projects\/([0-9a-f-]+)$/i)?.[1]
  const activePage = location.pathname.startsWith('/clients') ? 'Clients'
    : location.pathname.startsWith('/approvals') ? 'Approvals'
    : location.pathname.startsWith('/requests') ? 'Requests'
    : location.pathname.startsWith('/projects') ? 'Projects'
      : location.pathname.startsWith('/invoices') ? 'Invoices'
        : location.pathname.startsWith('/settings') ? 'Settings'
          : location.pathname.startsWith('/account') ? 'Account'
            : 'Overview'

  const admin = user?.role === 'Admin'
  const workspaceName = admin ? workspaceSettings.name : user?.workspaceName ?? 'Client workspace'
  const supportEmail = admin ? workspaceSettings.supportEmail : user?.supportEmail ?? ''
  const changeTheme = (lightMode: boolean) => {
    const nextTheme = lightMode ? 'light' : 'dark'
    localStorage.setItem('clientry-theme', nextTheme)
    setTheme(nextTheme)
  }
  const navigateTo = (page: string) => {
    setSelectedProject(null)
    setOpenPopover(null)
    routerNavigate(page === 'Overview' ? '/' : `/${page.toLowerCase()}`)
  }
  const openProject = (project: Project) => {
    setSelectedProject(project)
    setOpenPopover(null)
    routerNavigate(`/projects/${project.id}`)
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

  useEffect(() => {
    document.documentElement.dataset.theme = theme
  }, [theme])

  const profileQuery = useQuery({
    queryKey: ['profile', token],
    queryFn: () => request<{ id: string; name: string; role: User['role']; workspaceName: string; supportEmail: string }>('/auth/me', token),
    enabled: Boolean(token),
  })
  const workspaceQuery = useQuery({
    queryKey: ['workspace', token, user?.role],
    enabled: Boolean(token && user),
    queryFn: async () => {
      const [projectData, invoiceData] = await Promise.all([
        request<{ items: Project[] }>('/projects?page=1&pageSize=100', token),
        request<{ items: Invoice[] }>('/invoices?page=1&pageSize=100', token),
      ])
      if (user?.role !== 'Admin') return { projects: projectData.items, invoices: invoiceData.items }
      const [dashboard, clientData, settings] = await Promise.all([
        request<Metrics>('/admin/dashboard', token),
        request<{ items: ClientSummary[] }>('/admin/clients?page=1&pageSize=100', token),
        request<AdminWorkspaceSettings>('/admin/workspace-settings', token),
      ])
      return {
        projects: projectData.items, invoices: invoiceData.items,
        metrics: dashboard, clients: clientData.items, settings,
      }
    },
  })
  const preferencesQuery = useQuery({
    queryKey: ['notification-preferences', token],
    queryFn: () => request<NotificationPreferences>('/notifications/preferences', token),
    enabled: Boolean(token),
  })
  const approvalsQuery = useQuery({
    queryKey: ['approvals', token],
    queryFn: () => request<{ items: ApprovalItem[]; total: number }>('/approvals?page=1&pageSize=100', token),
    enabled: Boolean(token && user?.role === 'Client'),
  })
  const clientOverviewQuery = useQuery({
    queryKey: ['client-overview', token],
    queryFn: () => request<ClientOverviewData>('/me/overview', token),
    enabled: Boolean(token && user?.role === 'Client'),
  })
  const projectContentQuery = useQuery({
    queryKey: ['project-content', token, selectedProject?.id],
    queryFn: async () => {
      const [comments, files] = await Promise.all([
        request<{ items: Comment[] }>(`/projects/${selectedProject?.id}/comments?page=1&pageSize=100`, token),
        request<{ items: ProjectFile[] }>(`/projects/${selectedProject?.id}/files?page=1&pageSize=100`, token),
      ])
      return { comments: comments.items, files: files.items }
    },
    enabled: Boolean(token && selectedProject),
  })
  const loading = profileQuery.isLoading || workspaceQuery.isLoading

  useEffect(() => {
    if (!profileQuery.data) return
    setUser({
      id: profileQuery.data.id, name: profileQuery.data.name, role: profileQuery.data.role,
      workspaceName: profileQuery.data.workspaceName, supportEmail: profileQuery.data.supportEmail,
    })
  }, [profileQuery.data])

  useEffect(() => {
    if (!profileQuery.isError) return
    signOut()
  // signOut intentionally follows the stored token lifetime.
  // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [profileQuery.isError])

  useEffect(() => {
    if (workspaceQuery.data) {
      setProjects(workspaceQuery.data.projects)
      setInvoices(workspaceQuery.data.invoices)
      if ('metrics' in workspaceQuery.data && workspaceQuery.data.metrics &&
        workspaceQuery.data.clients && workspaceQuery.data.settings) {
        setMetrics(workspaceQuery.data.metrics)
        setClients(workspaceQuery.data.clients)
        setWorkspaceSettings(workspaceQuery.data.settings)
      }
    }
    if (workspaceQuery.error)
      setError(workspaceQuery.error instanceof Error ? workspaceQuery.error.message : 'Unable to load your workspace.')
  }, [workspaceQuery.data, workspaceQuery.error])

  useEffect(() => {
    if (preferencesQuery.data) {
      setWorkspaceUpdates(preferencesQuery.data.workspaceUpdates)
      setInvoiceReminders(preferencesQuery.data.invoiceReminders)
    }
    if (preferencesQuery.error)
      setError(preferencesQuery.error instanceof Error ? preferencesQuery.error.message : 'Unable to load notification preferences.')
  }, [preferencesQuery.data, preferencesQuery.error])

  useEffect(() => {
    if (projectContentQuery.data) {
      setComments(projectContentQuery.data.comments)
      setProjectFiles(projectContentQuery.data.files)
    }
    if (projectContentQuery.error)
      setError(projectContentQuery.error instanceof Error ? projectContentQuery.error.message : 'Unable to load project details.')
  }, [projectContentQuery.data, projectContentQuery.error])

  useEffect(() => {
    if (!projectRouteId || !token) {
      if (!projectRouteId) setSelectedProject(null)
      return
    }
    const project = projects.find((item) => item.id === projectRouteId)
    if (project) {
      setSelectedProject(project)
      return
    }
    request<Project>(`/projects/${projectRouteId}`, token)
      .then(setSelectedProject)
      .catch((reason: unknown) => setError(reason instanceof Error ? reason.message : 'Unable to load project.'))
  }, [projectRouteId, projects, token])

  const signOut = () => {
    sessionStorage.removeItem('clienthub-token')
    setToken('')
    setUser(null)
    setSelectedProject(null)
    setNotificationItems([])
    setUnreadNotificationCount(0)
    routerNavigate('/login')
  }

  async function loadNotifications() {
    setNotificationLoading(true)
    try {
      const result = await request<{ items: NotificationItem[]; unreadCount: number }>('/notifications?page=1&pageSize=10', token)
      setNotificationItems(result.items)
      setUnreadNotificationCount(result.unreadCount)
    } catch (reason) {
      setError(reason instanceof Error ? reason.message : 'Unable to load notifications.')
    } finally {
      setNotificationLoading(false)
    }
  }

  async function markNotificationRead(notificationId: string) {
    const wasUnread = notificationItems.some((item) => item.id === notificationId && !item.isRead)
    try {
      await request<void>(`/notifications/${notificationId}/read`, token, { method: 'PATCH' })
      setNotificationItems((items) => items.map((item) => item.id === notificationId ? { ...item, isRead: true } : item))
      if (wasUnread) setUnreadNotificationCount((count) => Math.max(0, count - 1))
    } catch (reason) {
      setError(reason instanceof Error ? reason.message : 'Unable to update notification.')
    }
  }

  async function markAllNotificationsRead() {
    try {
      await request<void>('/notifications/read-all', token, { method: 'POST' })
      setNotificationItems((items) => items.map((item) => ({ ...item, isRead: true })))
      setUnreadNotificationCount(0)
    } catch (reason) {
      setError(reason instanceof Error ? reason.message : 'Unable to update notifications.')
    }
  }

  async function saveNotificationPreferences(next: NotificationPreferences) {
    try {
      await request<void>('/notifications/preferences', token, { method: 'PUT', body: JSON.stringify(next) })
      setWorkspaceUpdates(next.workspaceUpdates)
      setInvoiceReminders(next.invoiceReminders)
    } catch (reason) {
      setError(reason instanceof Error ? reason.message : 'Unable to save notification preferences.')
    }
  }

  async function saveWorkspaceSettings(name: string, supportEmail: string) {
    try {
      await request<void>('/admin/workspace-settings', token, {
        method: 'PUT',
        body: JSON.stringify({ name, supportEmail }),
      })
      setWorkspaceSettings({ name, supportEmail })
    } catch (reason) {
      setError(reason instanceof Error ? reason.message : 'Unable to save workspace settings.')
    }
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
      setIsRegisterMode(false)
      routerNavigate('/')
    } catch (reason) {
      setLoginError(reason instanceof Error ? reason.message : 'Sign in failed.')
    }
  }

  async function registerUser(event: FormEvent) {
    event.preventDefault()
    setLoginError('')
    try {
      const result = await request<{ accessToken: string; name: string; role: User['role'] }>('/auth/register', '', {
        method: 'POST', body: JSON.stringify({ email, password, fullName }),
      })
      sessionStorage.setItem('clienthub-token', result.accessToken)
      setUser({ name: result.name, role: result.role })
      setToken(result.accessToken)
      setIsRegisterMode(false)
      routerNavigate('/')
    } catch (reason) {
      setLoginError(reason instanceof Error ? reason.message : 'Account creation failed.')
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
        id: saved.id, authorId: user?.id ?? '', body: commentText, createdAt: new Date().toISOString(), author: user?.name ?? 'You',
      }])
      setCommentText('')
    } catch (reason) {
      setError(reason instanceof Error ? reason.message : 'Unable to post your update.')
    }
  }

  async function refreshProject(projectId: string): Promise<void> {
    try {
      const updated = await request<Project>(`/projects/${projectId}`, token)
      setSelectedProject(updated)
      void queryClient.invalidateQueries({ queryKey: ['workspace', token] })
    } catch (reason) {
      setError(reason instanceof Error ? reason.message : 'Unable to refresh this project.')
    }
  }

  async function uploadFile(file: File, onProgress: (progress: number) => void) {
    if (!selectedProject) throw new Error('Choose a project before uploading.')
    await new Promise<void>((resolve, reject) => {
      const data = new FormData()
      data.append('file', file)
      const upload = new XMLHttpRequest()
      upload.open('POST', `${apiBase}/api/projects/${selectedProject.id}/files`)
      upload.setRequestHeader('Authorization', `Bearer ${token}`)
      upload.upload.onprogress = (event) => {
        if (event.lengthComputable) onProgress(Math.round(event.loaded / event.total * 100))
      }
      upload.onerror = () => reject(new Error('Network error while uploading this file.'))
      upload.onload = () => {
        if (upload.status < 200 || upload.status >= 300) {
          let message = `Upload failed (${upload.status}).`
          try { message = JSON.parse(upload.responseText).message ?? message } catch { /* use the HTTP status */ }
          reject(new Error(message))
          return
        }
        onProgress(100)
        resolve()
      }
      upload.send(data)
    })
    const result = await request<{ items: ProjectFile[] }>(`/projects/${selectedProject.id}/files?page=1&pageSize=100`, token)
    setProjectFiles(result.items)
  }

  async function downloadProjectZip(projectId: string) {
    const response = await fetch(`${apiBase}/api/projects/${projectId}/files.zip`, {
      headers: { Authorization: `Bearer ${token}` },
    })
    if (!response.ok) {
      const body = await response.json().catch(() => null) as { message?: string } | null
      throw new Error(body?.message ?? `Archive download failed (${response.status}).`)
    }
    const url = URL.createObjectURL(await response.blob())
    const anchor = document.createElement('a'); anchor.href = url; anchor.download = 'project-files.zip'; anchor.click()
    URL.revokeObjectURL(url)
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

  const greeting = useMemo(() => {
    const hour = new Date().getHours()
    const timeOfDay = hour < 12 ? 'Good morning' : hour < 18 ? 'Good afternoon' : 'Good evening'
    return `${timeOfDay}, ${user?.name.split(' ')[0] ?? 'there'}`
  }, [user])
  if (!token || !user) return <Login
    email={email}
    setEmail={setEmail}
    password={password}
    setPassword={setPassword}
    fullName={fullName}
    setFullName={setFullName}
    setIsRegisterMode={setIsRegisterMode}
    onSubmit={isRegisterMode ? registerUser : logIn}
    onToggleMode={() => { setIsRegisterMode((current) => !current); setLoginError('') }}
    isRegisterMode={isRegisterMode}
    error={loginError}
  />

  return (
    <div className="app-shell">
      <aside className="sidebar">
        <div className="brand"><div className="brand-mark">c</div><span>clientry</span></div>
        <div className="workspace-container" ref={workspaceRef}>
          <button className={`workspace-switch ${openPopover === 'workspace' ? 'open' : ''}`} aria-expanded={openPopover === 'workspace'} aria-controls="workspace-popover" onClick={() => setOpenPopover(openPopover === 'workspace' ? null : 'workspace')}>
            <span className="workspace-avatar"><Icon name="building" /></span>
            <span className="workspace-meta"><strong>{workspaceName}</strong><span>{admin ? 'Agency workspace' : 'Client workspace'}</span></span>
            <Icon name="chevron" />
          </button>
          {openPopover === 'workspace' && <div className="popover workspace-popover" id="workspace-popover">
            <div className="popover-heading"><span className="popover-icon"><Icon name="building" /></span><div><strong>{workspaceName}</strong><span>Current workspace</span></div></div>
            <p>{admin ? 'Manage your studio and client relationships.' : 'Your projects, files, and invoices in one place.'}</p>
            {admin && <button className="popover-link" onClick={() => navigateTo('Clients')}>View clients <Icon name="arrow" /></button>}
            <button className="popover-link" onClick={() => navigateTo('Settings')}>Workspace settings <Icon name="arrow" /></button>
          </div>}
        </div>
        <div className="nav-label">WORKSPACE</div>
        <nav className="nav-list" aria-label="Workspace navigation">
          {(admin ? ['Overview', 'Projects', 'Invoices', 'Clients', 'Requests'] : ['Overview', 'Projects', 'Approvals', 'Requests', 'Invoices']).map((item) => (
            <button key={item} className={`nav-item ${activePage === item ? 'active' : ''}`} aria-current={activePage === item ? 'page' : undefined} onClick={() => navigateTo(item)}>
              <Icon name={item === 'Overview' ? 'grid' : item === 'Projects' ? 'layers' : item === 'Approvals' ? 'check' : item === 'Invoices' ? 'file' : item === 'Requests' ? 'upload' : 'users'} />{item}
              {item === 'Invoices' && invoices.some((invoice) => invoice.status === 'Sent' || invoice.status === 'Overdue') && <span className="nav-count">{invoices.filter((invoice) => invoice.status === 'Sent' || invoice.status === 'Overdue').length}</span>}
              {item === 'Approvals' && (approvalsQuery.data?.total ?? 0) > 0 && <span className="nav-count">{approvalsQuery.data?.total}</span>}
            </button>
          ))}
        </nav>
        <div className="sidebar-bottom">
          <button className={`nav-item muted ${activePage === 'Settings' ? 'active' : ''}`} aria-current={activePage === 'Settings' ? 'page' : undefined} onClick={() => navigateTo('Settings')}><Icon name="settings" />Settings</button>
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
            <button className="icon-button search-button" aria-label="Search workspace" aria-expanded={openPopover === 'search'} aria-controls="workspace-search-popover" onClick={() => { setSearchTerm(''); setOpenPopover(openPopover === 'search' ? null : 'search') }}><Icon name="search" /></button>
            <button className="icon-button notification-button" aria-label={`Notifications${unreadNotificationCount ? `, ${unreadNotificationCount} unread` : ''}`} aria-expanded={openPopover === 'notifications'} aria-controls="workspace-notifications-popover" onClick={() => { const opening = openPopover !== 'notifications'; setOpenPopover(opening ? 'notifications' : null); if (opening) void loadNotifications() }}><Icon name="bell" />{unreadNotificationCount > 0 && <i />}</button>
            <div className="topbar-divider" />
            <button className="topbar-avatar" aria-label={`Open ${user.name}'s account`} onClick={() => navigateTo('Account')}>{user.name.split(' ').map((part) => part[0]).join('').slice(0, 2)}</button>
            {openPopover === 'search' && <div className="popover search-popover" id="workspace-search-popover" role="dialog" aria-label="Search workspace">
              <label className="search-field"><Icon name="search" /><input ref={searchInputRef} value={searchTerm} onChange={(event) => setSearchTerm(event.target.value)} onKeyDown={(event) => { if (event.key === 'Escape') setOpenPopover(null) }} placeholder="Search projects, invoices, clients..." /><button className="icon-button" onClick={() => setOpenPopover(null)} aria-label="Close search"><Icon name="close" /></button></label>
              <SearchResults term={searchTerm} projects={projects} invoices={invoices} clients={admin ? clients : []} onOpenProject={openProject} onOpenInvoices={() => navigateTo('Invoices')} onOpenClients={() => navigateTo('Clients')} />
            </div>}
            {openPopover === 'notifications' && <NotificationsPopover items={notificationItems} unreadCount={unreadNotificationCount} loading={notificationLoading} onMarkRead={(id) => void markNotificationRead(id)} onMarkAllRead={() => void markAllNotificationsRead()} />}
          </div>
        </header>

        <div className="content">
          {error && <div className="alert" role="alert">{error}<button onClick={() => setError('')} aria-label="Dismiss error">×</button></div>}
          {loading && <div className="loading-bar" role="status" aria-label="Loading workspace" />}
          {selectedProject ? (
            <ProjectDetail project={selectedProject} comments={comments} setComments={setComments} files={projectFiles} setFiles={setProjectFiles} token={token} currentUserId={user.id ?? ''} isAdmin={Boolean(admin)} commentText={commentText} setCommentText={setCommentText} onSubmit={addComment} onUpload={uploadFile} onDownload={downloadFile} onDownloadZip={downloadProjectZip} onProjectChanged={() => refreshProject(selectedProject.id)} onBack={() => navigateTo('Projects')} />
          ) : activePage === 'Overview' ? (
            <Overview greeting={greeting} admin={admin} projects={projects} invoices={invoices} metrics={metrics} supportEmail={supportEmail} clientOverview={clientOverviewQuery.data ?? null} clientOverviewLoading={clientOverviewQuery.isLoading} clientOverviewError={clientOverviewQuery.error instanceof Error ? clientOverviewQuery.error.message : ''} onOpenProject={openProject} onOpenProjects={() => navigateTo('Projects')} onOpenInvoices={() => navigateTo('Invoices')} onOpenApprovals={() => navigateTo('Approvals')} onOpenNotifications={() => { setOpenPopover('notifications'); void loadNotifications() }} onCreateProject={() => setCreatingProject(true)} />
          ) : activePage === 'Projects' ? (
            <Projects projects={projects} admin={Boolean(admin)} onOpenProject={openProject} />
          ) : activePage === 'Approvals' ? (
            <Approvals items={approvalsQuery.data?.items ?? []} loading={approvalsQuery.isLoading} error={approvalsQuery.error instanceof Error ? approvalsQuery.error.message : ''} token={token} onChanged={() => void queryClient.invalidateQueries({ queryKey: ['approvals', token] })} />
          ) : activePage === 'Requests' ? (
            <Requests token={token} admin={Boolean(admin)} clients={clients} path={location.pathname} onOpenProject={(id) => {
              const project = projects.find((item) => item.id === id)
              if (project) openProject(project)
            }} />
          ) : activePage === 'Invoices' ? (
            <Invoices invoices={invoices} token={token} admin={Boolean(admin)} clients={clients} onChanged={() => void queryClient.invalidateQueries({ queryKey: ['workspace', token] })} />
          ) : activePage === 'Settings' ? (
            <Settings key={`${workspaceSettings.name}:${workspaceSettings.supportEmail}`} workspaceName={workspaceName} admin={Boolean(admin)} workspaceSettings={workspaceSettings} onSaveWorkspaceSettings={(name, supportEmail) => void saveWorkspaceSettings(name, supportEmail)} lightMode={theme === 'light'} onLightModeChange={changeTheme} workspaceUpdates={workspaceUpdates} invoiceReminders={invoiceReminders} onWorkspaceUpdatesChange={(enabled) => void saveNotificationPreferences({ workspaceUpdates: enabled, invoiceReminders })} onInvoiceRemindersChange={(enabled) => void saveNotificationPreferences({ workspaceUpdates, invoiceReminders: enabled })} />
          ) : activePage === 'Account' ? (
            location.pathname === '/account/billing'
              ? <ClientBilling token={token} />
              : <Account user={user} workspaceName={workspaceName} admin={Boolean(admin)} onBilling={() => routerNavigate('/account/billing')} onSignOut={signOut} />
          ) : (
            <Clients clients={clients} token={token} onChanged={() => void queryClient.invalidateQueries({ queryKey: ['workspace', token] })} />
          )}
        </div>
      </main>
      {creatingProject && <ProjectCreateModal clients={clients} token={token} onClose={() => setCreatingProject(false)} onCreated={() => { setCreatingProject(false); void queryClient.invalidateQueries({ queryKey: ['workspace', token] }) }} />}
    </div>
  )
}

function Login({ email, setEmail, password, setPassword, fullName, setFullName, onSubmit, onToggleMode, isRegisterMode, setIsRegisterMode, error }: {
  email: string; setEmail: (value: string) => void; password: string; setPassword: (value: string) => void
  fullName: string; setFullName: (value: string) => void; onSubmit: (event: FormEvent) => void
  onToggleMode: () => void; isRegisterMode: boolean; setIsRegisterMode: (value: boolean) => void; error: string
}) {
  return <main className="login-page">
    <section className="login-card">
      <div className="brand login-brand"><div className="brand-mark">c</div><span>clientry</span></div>
      <div className="login-copy"><div className="eyebrow">YOUR WORK, IN ONE PLACE</div><h1>{isRegisterMode ? 'Create your account.' : 'Welcome back.'}</h1><p>{isRegisterMode ? 'Set up your workspace and start collaborating.' : 'Sign in to pick up where you left off.'}</p></div>
      <form className="login-form" onSubmit={onSubmit}>
        {isRegisterMode && <>
          <label htmlFor="fullName">Full name</label>
          <input id="fullName" type="text" autoComplete="name" value={fullName} onChange={(event) => setFullName(event.target.value)} required />
        </>}
        <label htmlFor="email">Email address</label><input id="email" type="email" autoComplete="username" value={email} onChange={(event) => setEmail(event.target.value)} required />
        <div className="password-label"><label htmlFor="password">Password</label><span>{isRegisterMode ? 'Use 8+ characters' : 'Enter your password'}</span></div>
        <input id="password" type="password" autoComplete={isRegisterMode ? 'new-password' : 'current-password'} value={password} onChange={(event) => setPassword(event.target.value)} required />
        {error && <div className="login-error" role="alert">{error}</div>}
        <button className="primary-button login-submit" type="submit">{isRegisterMode ? 'Create account' : 'Sign in'} <Icon name="arrow" /></button>
      </form>
      <button className="text-button login-toggle" type="button" onClick={onToggleMode}>
        {isRegisterMode ? 'Already have an account? Sign in' : 'Need an account? Create one'}
      </button>
      <div className="login-demo"><span>Try a demo workspace</span><div><button type="button" onClick={() => { setEmail('clientA@demo.com'); setPassword('DemoPass!234'); setIsRegisterMode(false) }}>Client portal</button><button type="button" onClick={() => { setEmail('admin@demo.com'); setPassword('DemoPass!234'); setIsRegisterMode(false) }}>Agency admin</button></div></div>
      <p className="login-footer">Secure client collaboration, made simple.</p>
    </section>
    <aside className="login-art" aria-hidden="true"><div className="art-glow" /><div className="art-orbit orbit-one" /><div className="art-orbit orbit-two" /><div className="art-card"><div className="art-card-head"><span className="art-dot" /><span>CLIENTRY WORKSPACE</span></div><div className="art-brand-mark">c</div><strong>Good work, connected.</strong><small>Projects, conversations, files, and invoices.</small><div className="art-progress"><i /></div><div className="art-progress-label"><span>One shared workspace</span></div></div><div className="art-note"><span className="note-avatar"><Icon name="layers" /></span><div><strong>Everything in one place</strong><p>Keep projects and updates together.</p></div></div><div className="art-stamp">Thoughtfully<br />connected.</div></aside>
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

function NotificationsPopover({ items, unreadCount, loading, onMarkRead, onMarkAllRead }: {
  items: NotificationItem[]
  unreadCount: number
  loading: boolean
  onMarkRead: (id: string) => void
  onMarkAllRead: () => void
}) {
  return <div className="popover notifications-popover" id="workspace-notifications-popover" role="dialog" aria-label="Workspace notifications">
    <div className="popover-title"><div><strong>Notifications</strong><span>Your workspace updates</span></div><span className="notification-count">{unreadCount}</span></div>
    {loading ? <p className="popover-hint">Loading notifications...</p> :
      items.length ? <div className="notification-list">{items.map((item) => <button key={item.id} className={`notification-item${item.isRead ? '' : ' unread'}`} onClick={() => onMarkRead(item.id)}>
        <span className="notification-mark"><Icon name="bell" /></span><span className="notification-copy"><strong>{item.type}</strong><span>{item.message}</span><small>{relativeDate(item.createdAt)}</small></span>
      </button>)}</div> : <p className="popover-hint">You’re all caught up. New workspace updates will appear here.</p>}
    {unreadCount > 0 && <button className="popover-link" onClick={onMarkAllRead}>Mark all as read</button>}
  </div>
}

function Settings({ workspaceName, admin, workspaceSettings, onSaveWorkspaceSettings, lightMode, onLightModeChange, workspaceUpdates, invoiceReminders, onWorkspaceUpdatesChange, onInvoiceRemindersChange }: {
  workspaceName: string
  admin: boolean
  workspaceSettings: AdminWorkspaceSettings
  onSaveWorkspaceSettings: (name: string, supportEmail: string) => void
  lightMode: boolean
  onLightModeChange: (enabled: boolean) => void
  workspaceUpdates: boolean
  invoiceReminders: boolean
  onWorkspaceUpdatesChange: (enabled: boolean) => void
  onInvoiceRemindersChange: (enabled: boolean) => void
}) {
  const [name, setName] = useState(workspaceSettings.name)
  const [supportEmail, setSupportEmail] = useState(workspaceSettings.supportEmail)
  return <div className="page-enter">
    <div className="page-heading"><div><div className="eyebrow">PREFERENCES</div><h1>Settings<span className="heading-period">.</span></h1><p>Choose how you’d like to use {workspaceName}.</p></div></div>
    <section className="panel settings-panel">
      <div className="panel-heading"><div><h2>Appearance & notifications</h2><p>Notification preferences are saved to your account.</p></div><span className="settings-badge">PERSONAL</span></div>
      <PreferenceToggle title="Light mode" description="Use a bright color palette throughout the workspace." checked={lightMode} onChange={onLightModeChange} />
      <PreferenceToggle title="Workspace updates" description="Show project and activity updates in your notification menu." checked={workspaceUpdates} onChange={onWorkspaceUpdatesChange} />
      <PreferenceToggle title="Invoice reminders" description="Keep upcoming and overdue invoices on your radar." checked={invoiceReminders} onChange={onInvoiceRemindersChange} />
    </section>
    {admin && <form className="panel settings-panel workspace-settings-form" onSubmit={(event) => { event.preventDefault(); onSaveWorkspaceSettings(name, supportEmail) }}>
      <div className="panel-heading"><div><h2>Workspace details</h2><p>These details are shared across your client workspace.</p></div><span className="settings-badge">ADMIN</span></div>
      <label className="form-field"><span>Workspace name</span><input value={name} onChange={(event) => setName(event.target.value)} maxLength={120} required /></label>
      <label className="form-field"><span>Support email</span><input type="email" value={supportEmail} onChange={(event) => setSupportEmail(event.target.value)} maxLength={254} required /></label>
      <button className="primary-button" type="submit">Save workspace details <Icon name="arrow" /></button>
    </form>}
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

function Account({ user, workspaceName, admin, onBilling, onSignOut }: {
  user: User; workspaceName: string; admin: boolean; onBilling: () => void; onSignOut: () => void
}) {
  return <div className="page-enter">
    <div className="page-heading"><div><div className="eyebrow">YOUR PROFILE</div><h1>Account<span className="heading-period">.</span></h1><p>Your account and workspace details.</p></div></div>
    <section className="panel account-panel">
      <div className="account-identity"><span className="account-avatar">{user.name.split(' ').map((part) => part[0]).join('').slice(0, 2)}</span><div><h2>{user.name}</h2><p>{user.role}</p></div></div>
      <div className="account-detail"><span>Account name</span><strong>{user.name}</strong></div>
      <div className="account-detail"><span>Role</span><strong>{user.role === 'Admin' ? 'Agency admin' : 'Client'}</strong></div>
      <div className="account-detail"><span>Workspace</span><strong>{workspaceName}</strong></div>
      {!admin && <button className="cancel-button account-signout" onClick={onBilling}>Billing details <Icon name="arrow" /></button>}
      <button className="cancel-button account-signout" onClick={onSignOut}><Icon name="logout" /> Sign out</button>
    </section>
  </div>
}

function ClientBilling({ token }: { token: string }) {
  const navigate = useNavigate()
  const billingQuery = useQuery({
    queryKey: ['billing-details', token],
    queryFn: () => request<{ legalName?: string; taxId?: string; billingEmails?: string; billingAddress?: string; canEdit: boolean }>('/me/billing', token),
    enabled: Boolean(token),
  })
  const [legalName, setLegalName] = useState('')
  const [taxId, setTaxId] = useState('')
  const [billingEmails, setBillingEmails] = useState('')
  const [billingAddress, setBillingAddress] = useState('')
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState('')
  const [saved, setSaved] = useState(false)
  useEffect(() => {
    if (!billingQuery.data) return
    setLegalName(billingQuery.data.legalName ?? '')
    setTaxId(billingQuery.data.taxId ?? '')
    setBillingEmails(billingQuery.data.billingEmails ?? '')
    setBillingAddress(billingQuery.data.billingAddress ?? '')
  }, [billingQuery.data])
  async function save(event: FormEvent) {
    event.preventDefault()
    setBusy(true); setError(''); setSaved(false)
    try {
      await request('/me/billing', token, {
        method: 'PUT', body: JSON.stringify({ legalName, taxId, billingEmails, billingAddress }),
      })
      await billingQuery.refetch()
      setSaved(true)
    } catch (reason) { setError(reason instanceof Error ? reason.message : 'Unable to save billing details.') }
    finally { setBusy(false) }
  }
  return <div className="page-enter">
    <div className="page-heading"><div><div className="eyebrow">ACCOUNT</div><h1>Billing details<span className="heading-period">.</span></h1><p>These details appear on invoices and paid receipts.</p></div><button className="cancel-button" onClick={() => navigate('/account')}>Back to account</button></div>
    {error && <div className="alert" role="alert">{error}</div>}
    {billingQuery.isError && <div className="alert" role="alert">{billingQuery.error instanceof Error ? billingQuery.error.message : 'Unable to load billing details.'}</div>}
    {billingQuery.isLoading ? <section className="panel full-panel"><div className="loading-bar" role="status" aria-label="Loading billing details" /></section> :
      <form className="panel request-form billing-form" onSubmit={(event) => void save(event)}>
        <label>Legal name<input value={legalName} onChange={(event) => setLegalName(event.target.value)} maxLength={160} readOnly={!billingQuery.data?.canEdit} /></label>
        <label>Tax ID<input value={taxId} onChange={(event) => setTaxId(event.target.value)} maxLength={80} readOnly={!billingQuery.data?.canEdit} /></label>
        <label>Billing email(s)<input type="text" value={billingEmails} onChange={(event) => setBillingEmails(event.target.value)} maxLength={500} placeholder="billing@example.com; finance@example.com" readOnly={!billingQuery.data?.canEdit} /></label>
        <label>Billing address<textarea value={billingAddress} onChange={(event) => setBillingAddress(event.target.value)} maxLength={1000} rows={4} readOnly={!billingQuery.data?.canEdit} /></label>
        {saved && <p className="billing-saved" role="status">Billing details saved.</p>}
        {!billingQuery.data?.canEdit && <p className="request-file-hint">Only the client owner can change billing details.</p>}
        {billingQuery.data?.canEdit && <div className="request-form-actions"><button className="primary-button" disabled={busy}>{busy ? 'Saving…' : 'Save billing details'}</button></div>}
      </form>}
  </div>
}

function Overview({ greeting, admin, projects, invoices, metrics, supportEmail, clientOverview, clientOverviewLoading, clientOverviewError, onOpenProject, onOpenProjects, onOpenInvoices, onOpenApprovals, onOpenNotifications, onCreateProject }: {
  greeting: string; admin: boolean; projects: Project[]; invoices: Invoice[]; metrics: Metrics | null; supportEmail: string
  clientOverview: ClientOverviewData | null; clientOverviewLoading: boolean; clientOverviewError: string
  onOpenProject: (project: Project) => void; onOpenProjects: () => void; onOpenInvoices: () => void
  onOpenApprovals: () => void; onOpenNotifications: () => void; onCreateProject: () => void
}) {
  const active = projects.filter((project) => project.status !== 'Archived')
  const outstanding = invoices.filter((invoice) => invoice.status === 'Sent' || invoice.status === 'Overdue')
  return <div className="page-enter">
    <div className="page-heading"><div><div className="eyebrow">YOUR WORKSPACE</div><h1>{greeting}<span className="heading-period">.</span></h1><p>{admin ? 'Here’s what’s happening across your studio.' : 'Here’s the latest across your projects.'}</p></div>{admin && <button className="primary-button" onClick={onCreateProject}><span>＋</span> New project</button>}</div>
    {admin && <div className="stats-grid">
      <StatCard label={admin ? 'Active projects' : 'Your projects'} value={admin ? metrics?.activeProjects : active.length} hint={admin ? 'Across all clients' : 'Currently in progress'} accent="lavender" />
      <StatCard label={admin ? 'Revenue this month' : 'Open invoices'} value={admin ? money(metrics?.revenueThisMonth ?? 0) : outstanding.length} hint={admin ? 'From paid invoices' : outstanding.length ? 'One needs your attention' : 'You’re all caught up'} accent="mint" />
      <StatCard label={admin ? 'Unpaid invoices' : 'Next milestone'} value={admin ? metrics?.unpaidInvoices : (active[0]?.milestones.filter((item) => !item.isCompleted).length ?? 0)} hint={admin ? `${metrics?.overdueInvoices ?? 0} overdue` : 'Coming up in your projects'} accent="peach" />
      <StatCard label={admin ? 'Clients' : 'Completed milestones'} value={admin ? metrics?.clientCount : active.reduce((sum, project) => sum + project.milestones.filter((item) => item.isCompleted).length, 0)} hint={admin ? 'Active relationships' : 'A little progress adds up'} accent="blue" />
    </div>}
    {!admin && <section className="attention-section" aria-labelledby="needs-attention-title">
      <div className="panel-heading"><div><h2 id="needs-attention-title">Needs your attention</h2><p>Important updates and next steps across your workspace.</p></div></div>
      {clientOverviewError && <div className="alert" role="alert">{clientOverviewError}</div>}
      {clientOverviewLoading && !clientOverview && <div className="approval-skeletons" role="status" aria-label="Loading items needing attention"><i /><i /><i /></div>}
      <div className="attention-cards">
        <button className="attention-card" onClick={onOpenApprovals}><span>Pending approvals</span><strong>{clientOverview?.pendingApprovals.length ?? '—'}</strong><small>{clientOverview?.pendingApprovals.length ? 'Milestones waiting for your decision' : 'Nothing to review right now'}</small></button>
        <button className="attention-card" onClick={onOpenInvoices}><span>Unpaid invoices</span><strong>{clientOverview ? money(clientOverview.unpaidInvoices.reduce((sum, invoice) => sum + invoice.amount, 0)) : '—'}</strong><small>{clientOverview ? `${clientOverview.unpaidInvoices.length} invoice${clientOverview.unpaidInvoices.length === 1 ? '' : 's'} due or overdue` : 'Loading invoice totals'}</small></button>
        <button className="attention-card" onClick={onOpenNotifications}><span>Unread notifications</span><strong>{clientOverview?.unreadNotificationCount ?? '—'}</strong><small>Recent updates from your agency</small></button>
      </div>
      <div className="attention-details">
        <section className="panel attention-detail-panel"><div className="panel-heading"><div><h2>Recent project activity</h2><p>Updates since your last visit.</p></div></div>
          {clientOverview?.recentProjectActivity.length ? <div className="attention-activity-list">{clientOverview.recentProjectActivity.map((activity, index) => {
            const project = projects.find((entry) => entry.id === activity.projectId)
            return <button className="attention-activity-item" key={`${activity.createdAt}-${index}`} disabled={!project} onClick={() => project && onOpenProject(project)}><span><strong>{activity.action}</strong><small>{activity.projectName}{activity.metadata ? ` · ${activity.metadata}` : ''}</small></span><time>{relativeDate(activity.createdAt)}</time></button>
          })}</div> : <EmptyState title="No new project activity" text="New updates will appear here after your next visit." />}
        </section>
        <section className="panel attention-detail-panel"><div className="panel-heading"><div><h2>Next milestones</h2><p>Upcoming dates across your projects.</p></div></div>
          {clientOverview?.nextMilestones.length ? <div className="attention-activity-list">{clientOverview.nextMilestones.map((milestone) => <button className="attention-activity-item" key={milestone.id} onClick={() => {
            const project = projects.find((entry) => entry.id === milestone.projectId)
            if (project) onOpenProject(project)
          }}><span><strong>{milestone.title}</strong><small>{milestone.projectName}</small></span><time>{new Date(milestone.dueDate).toLocaleDateString('en', { month: 'short', day: 'numeric' })}</time></button>)}</div> : <EmptyState title="No upcoming milestones" text="Future milestone dates will appear here." />}
        </section>
      </div>
    </section>}
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
    <footer className="page-footer"><span>Made for good work.</span>{supportEmail && <span>Need a hand? <a href={`mailto:${supportEmail}`}>Contact support</a></span>}</footer>
  </div>
}

function StatCard({ label, value, hint, accent }: { label: string; value?: string | number; hint: string; accent: string }) {
  return <article className={`stat-card stat-${accent}`}><div className="stat-top"><span>{label}</span><span className="stat-mark">↗</span></div><strong>{value ?? '—'}</strong><small>{hint}</small></article>
}

function ProjectRow({ project, onClick }: { project: Project; onClick: () => void }) {
  return <button className="project-row" onClick={onClick}><div className="project-icon"><Icon name="layers" /></div><div className="project-info"><strong>{project.name}</strong><span>{project.description}</span></div><div className="project-progress"><div className="progress-caption"><span>{project.progress}% complete</span><b>{project.dueDate ? new Date(project.dueDate).toLocaleDateString('en', { month: 'short', day: 'numeric' }) : 'No due date'}</b></div><div className="progress-track"><i style={{ width: `${project.progress}%` }} /></div></div><span className={`status-pill status-${project.status.toLowerCase()}`}>{project.status.replace(/([A-Z])/g, ' $1').trim()}</span><Icon name="arrow" /></button>
}

function Projects({ projects, admin, onOpenProject }: { projects: Project[]; admin: boolean; onOpenProject: (project: Project) => void }) {
  const [waitingOnClient, setWaitingOnClient] = useState(false)
  const visibleProjects = waitingOnClient
    ? projects.filter((project) => project.milestones.some((milestone) => milestone.approvalStatus === 'Pending'))
    : projects
  return <div className="page-enter">
    <div className="page-heading"><div><div className="eyebrow">YOUR WORKSPACE</div><h1>Projects<span className="heading-period">.</span></h1><p>All the moving pieces, in one clear view.</p></div>
      {admin && <label className="approval-filter"><input type="checkbox" checked={waitingOnClient} onChange={(event) => setWaitingOnClient(event.target.checked)} /> Waiting on client</label>}
    </div>
    <section className="panel full-panel">{visibleProjects.length ? visibleProjects.map((project) => <ProjectRow key={project.id} project={project} onClick={() => onOpenProject(project)} />) : <EmptyState title={waitingOnClient ? 'No approvals waiting' : 'No projects yet'} text={waitingOnClient ? 'Milestones awaiting a client decision will appear here.' : 'Your team will add a project here when work begins.'} />}</section>
  </div>
}

function Approvals({ items, loading, error, token, onChanged }: {
  items: ApprovalItem[]; loading: boolean; error: string; token: string; onChanged: () => void
}) {
  const [dialog, setDialog] = useState<{ item: ApprovalItem; decision: 'Approved' | 'ChangesRequested' } | null>(null)
  const [note, setNote] = useState('')
  const [actionError, setActionError] = useState('')
  const [busy, setBusy] = useState(false)

  async function submitDecision(event: FormEvent) {
    event.preventDefault()
    if (!dialog || (dialog.decision === 'ChangesRequested' && !note.trim())) return
    setBusy(true); setActionError('')
    try {
      await request(`/projects/${dialog.item.projectId}/milestones/${dialog.item.id}/approval-decision`, token, {
        method: 'POST', body: JSON.stringify({ decision: dialog.decision, note: note.trim() || null }),
      })
      setDialog(null); setNote(''); onChanged()
    } catch (reason) {
      setActionError(reason instanceof Error ? reason.message : 'Unable to save your decision.')
    } finally { setBusy(false) }
  }

  return <div className="page-enter">
    <div className="page-heading"><div><div className="eyebrow">CLIENT REVIEW</div><h1>Approvals<span className="heading-period">.</span></h1><p>Review work your agency has shared with you.</p></div></div>
    {(error || actionError) && <div className="alert" role="alert">{actionError || error}{actionError && <button onClick={() => setActionError('')} aria-label="Dismiss error">×</button>}</div>}
    <section className="panel full-panel approval-list">
      {loading ? <div className="approval-skeletons" role="status" aria-label="Loading approvals"><i /><i /><i /></div> :
        items.length ? items.map((item) => <article className="approval-item" key={item.id}>
          <div className="approval-item-copy"><span className="status-pill status-pending">Waiting for your review</span><h2>{item.title}</h2><p>{item.projectName}</p>
            <small>Requested {item.requestedAt ? relativeDate(item.requestedAt) : 'recently'}{item.dueDate ? ` · Due ${new Date(item.dueDate).toLocaleDateString('en', { month: 'short', day: 'numeric' })}` : ''}</small>
          </div>
          <div className="approval-actions">
            <button className="primary-button" onClick={() => { setDialog({ item, decision: 'Approved' }); setActionError('') }}>Approve</button>
            <button className="cancel-button" onClick={() => { setDialog({ item, decision: 'ChangesRequested' }); setNote(''); setActionError('') }}>Request changes</button>
          </div>
        </article>) : <EmptyState title="You’re all caught up" text="Milestones awaiting your review will appear here." />}
    </section>
    {dialog && <div className="modal-backdrop" role="presentation" onMouseDown={(event) => { if (event.target === event.currentTarget && !busy) setDialog(null) }}>
      <section className="payment-modal project-modal approval-dialog" role="dialog" aria-modal="true" aria-labelledby="approval-dialog-title">
        <button className="modal-close" onClick={() => setDialog(null)} aria-label="Close approval decision">×</button>
        <div className="eyebrow">MILESTONE REVIEW</div><h2 id="approval-dialog-title">{dialog.decision === 'Approved' ? 'Approve this milestone?' : 'Request changes'}</h2>
        <p className="payment-description">{dialog.item.title} · {dialog.item.projectName}</p>
        {dialog.decision === 'Approved' && <p className="approval-confirmation">Approving marks this milestone as complete.</p>}
        <form className="create-project-form" onSubmit={(event) => void submitDecision(event)}>
          {dialog.decision === 'ChangesRequested' && <label>What should be changed?<textarea value={note} onChange={(event) => setNote(event.target.value)} maxLength={2000} required /></label>}
          {actionError && <p className="payment-error" role="alert">{actionError}</p>}
          <div className="modal-actions"><button type="button" className="cancel-button" onClick={() => setDialog(null)} disabled={busy}>Cancel</button><button className="primary-button" disabled={busy || (dialog.decision === 'ChangesRequested' && !note.trim())}>{busy ? 'Saving…' : dialog.decision === 'Approved' ? 'Confirm approval' : 'Send request'}</button></div>
        </form>
      </section>
    </div>}
  </div>
}

function Requests({ token, admin, clients, path, onOpenProject }: {
  token: string; admin: boolean; clients: ClientSummary[]; path: string; onOpenProject: (id: string) => void
}) {
  const navigate = useNavigate()
  const queryClient = useQueryClient()
  const detailId = path.match(/^\/requests\/([0-9a-f-]+)$/i)?.[1]
  const creating = path === '/requests/new'
  const [statusFilter, setStatusFilter] = useState('')
  const [clientFilter, setClientFilter] = useState('')
  const [title, setTitle] = useState('')
  const [description, setDescription] = useState('')
  const [priority, setPriority] = useState<ProjectRequestItem['priority']>('Normal')
  const [files, setFiles] = useState<File[]>([])
  const [decision, setDecision] = useState<'Accepted' | 'Declined' | null>(null)
  const [note, setNote] = useState('')
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState('')
  const listQuery = useQuery({
    queryKey: ['requests', token, statusFilter, clientFilter],
    queryFn: () => request<{ items: ProjectRequestItem[]; total: number }>(
      `/requests?page=1&pageSize=100${statusFilter ? `&status=${encodeURIComponent(statusFilter)}` : ''}${admin && clientFilter ? `&clientId=${encodeURIComponent(clientFilter)}` : ''}`,
      token,
    ),
    enabled: !detailId && !creating,
  })
  const detailQuery = useQuery({
    queryKey: ['request', token, detailId],
    queryFn: () => request<ProjectRequestItem>(`/requests/${detailId}`, token),
    enabled: Boolean(detailId),
  })
  const item = detailQuery.data
  const refresh = async () => {
    await queryClient.invalidateQueries({ queryKey: ['requests', token] })
    if (detailId) await queryClient.invalidateQueries({ queryKey: ['request', token, detailId] })
  }

  async function uploadAttachments(requestId: string, selectedFiles: File[]) {
    if (selectedFiles.length > 3) throw new Error('You can add up to three files to a request.')
    for (const file of selectedFiles) {
      const body = new FormData()
      body.append('file', file)
      await request(`/requests/${requestId}/attachments`, token, { method: 'POST', body })
    }
  }

  async function submitRequest(event: FormEvent) {
    event.preventDefault()
    setBusy(true); setError('')
    try {
      const created = await request<{ id: string }>('/requests', token, {
        method: 'POST', body: JSON.stringify({ title, description, priority }),
      })
      try {
        await uploadAttachments(created.id, files)
      } catch (reason) {
        setError(`Your request was saved, but an attachment could not be uploaded: ${reason instanceof Error ? reason.message : 'Upload failed.'}`)
      }
      await refresh()
      navigate(`/requests/${created.id}`)
    } catch (reason) {
      setError(reason instanceof Error ? reason.message : 'Unable to create your request.')
    } finally { setBusy(false) }
  }

  async function performAction(action: 'cancel' | 'review' | 'convert' | 'decision', nextDecision?: 'Accepted' | 'Declined') {
    if (!detailId) return
    setBusy(true); setError('')
    try {
      if (action === 'cancel') await request(`/requests/${detailId}`, token, { method: 'DELETE' })
      if (action === 'review') await request(`/admin/requests/${detailId}/review`, token, { method: 'PATCH' })
      if (action === 'convert') {
        const result = await request<{ id: string }>(`/admin/requests/${detailId}/convert`, token, { method: 'POST' })
        await refresh()
        onOpenProject(result.id)
        return
      }
      if (action === 'decision' && nextDecision) {
        await request(`/admin/requests/${detailId}/decision`, token, {
          method: 'PATCH', body: JSON.stringify({ status: nextDecision, note }),
        })
        setDecision(null); setNote('')
      }
      await refresh()
    } catch (reason) {
      setError(reason instanceof Error ? reason.message : 'Unable to update this request.')
    } finally { setBusy(false) }
  }

  async function downloadAttachment(attachmentId: string, fileName: string) {
    setBusy(true); setError('')
    try {
      const response = await fetch(`${apiBase}/api/requests/${detailId}/attachments/${attachmentId}`, {
        headers: { Authorization: `Bearer ${token}` },
      })
      if (!response.ok) throw new Error(`Download failed (${response.status})`)
      const url = URL.createObjectURL(await response.blob())
      const link = document.createElement('a')
      link.href = url; link.download = fileName; link.click()
      URL.revokeObjectURL(url)
    } catch (reason) {
      setError(reason instanceof Error ? reason.message : 'Unable to download this attachment.')
    } finally { setBusy(false) }
  }

  if (creating) return <div className="page-enter">
    <div className="page-heading"><div><div className="eyebrow">PROJECT INTAKE</div><h1>New request<span className="heading-period">.</span></h1><p>Tell your agency what you need help with.</p></div></div>
    {error && <div className="alert" role="alert">{error}</div>}
    <form className="panel request-form" onSubmit={(event) => void submitRequest(event)}>
      <label>Request title<input value={title} onChange={(event) => setTitle(event.target.value)} maxLength={160} required /></label>
      <label>Describe the work<textarea value={description} onChange={(event) => setDescription(event.target.value)} maxLength={4000} rows={6} required /></label>
      <label>Priority<select value={priority} onChange={(event) => setPriority(event.target.value as ProjectRequestItem['priority'])}><option>Low</option><option>Normal</option><option>High</option></select></label>
      <label>Attachments (up to 3, 10 MB each)<input type="file" multiple accept=".pdf,.png,.jpg,.jpeg,.txt" onChange={(event) => setFiles(Array.from(event.target.files ?? []).slice(0, 3))} /></label>
      {files.length > 0 && <p className="request-file-hint">{files.length} file{files.length === 1 ? '' : 's'} selected</p>}
      <div className="request-form-actions"><button type="button" className="cancel-button" onClick={() => navigate('/requests')}>Cancel</button><button className="primary-button" disabled={busy || !title.trim() || !description.trim()}>{busy ? 'Submitting…' : 'Submit request'}</button></div>
    </form>
  </div>

  if (detailId) return <div className="page-enter">
    <div className="page-heading"><div><div className="eyebrow">PROJECT INTAKE</div><h1>Request details<span className="heading-period">.</span></h1><p>Track your request and the agency response.</p></div><button className="cancel-button" onClick={() => navigate('/requests')}>Back to requests</button></div>
    {error && <div className="alert" role="alert">{error}</div>}
    {detailQuery.isLoading ? <section className="panel full-panel"><div className="loading-bar" role="status" aria-label="Loading request" /></section> :
      detailQuery.isError || !item ? <section className="panel full-panel"><EmptyState title="Request not found" text="This request may have been removed or is not available to your account." /></section> :
        <section className="panel request-detail">
          <div className="request-detail-heading"><div><span className={`status-pill status-${item.status.toLowerCase()}`}>{item.status === 'InReview' ? 'In review' : item.status}</span><span className={`status-pill request-priority priority-${item.priority.toLowerCase()}`}>{item.priority} priority</span></div><time>{new Date(item.createdAt).toLocaleDateString()}</time></div>
          <h2>{item.title}</h2><p className="request-description">{item.description}</p>
          {admin && <p className="request-client-name">Client: {item.clientName}</p>}
          {item.adminNote && <div className="request-response"><strong>Agency response</strong><p>{item.adminNote}</p></div>}
          {item.convertedProjectId && <button className="text-button" onClick={() => onOpenProject(item.convertedProjectId!)}>Open created project <Icon name="arrow" /></button>}
          <div className="request-attachments"><strong>Attachments</strong>
            {item.attachments.length ? item.attachments.map((file) => <button key={file.id} className="request-attachment" onClick={() => void downloadAttachment(file.id, file.fileName)} disabled={busy}><span>{file.fileName}<small>{formatBytes(file.size)}</small></span><Icon name="arrow" /></button>) : <span className="request-file-hint">No attachments</span>}
          </div>
          {!admin && item.status === 'New' && item.attachments.length < 3 && <label className="request-add-files">Add attachments<input type="file" multiple accept=".pdf,.png,.jpg,.jpeg,.txt" onChange={(event) => {
            const selected = Array.from(event.target.files ?? []).slice(0, 3 - item.attachments.length)
            if (selected.length) {
              setBusy(true); setError('')
              void uploadAttachments(item.id, selected).then(refresh).catch((reason: unknown) => setError(reason instanceof Error ? reason.message : 'Unable to upload attachments.')).finally(() => setBusy(false))
            }
            event.currentTarget.value = ''
          }} /></label>}
          <div className="request-detail-actions">
            {!admin && item.status === 'New' && <button className="cancel-button" onClick={() => { if (window.confirm('Cancel this request?')) void performAction('cancel') }} disabled={busy}>Cancel request</button>}
            {admin && item.status === 'New' && <button className="cancel-button" onClick={() => void performAction('review')} disabled={busy}>Move to review</button>}
            {admin && ['New', 'InReview'].includes(item.status) && <><button className="primary-button" onClick={() => { setDecision('Accepted'); setNote('') }} disabled={busy}>Accept</button><button className="cancel-button" onClick={() => { setDecision('Declined'); setNote('') }} disabled={busy}>Decline</button></>}
            {admin && ['Accepted', 'InReview'].includes(item.status) && <button className="primary-button" onClick={() => void performAction('convert')} disabled={busy}>Convert to project</button>}
          </div>
        </section>}
    {decision && <div className="modal-backdrop" role="presentation" onMouseDown={(event) => { if (event.target === event.currentTarget && !busy) setDecision(null) }}>
      <section className="payment-modal project-modal approval-dialog" role="dialog" aria-modal="true" aria-labelledby="request-decision-title">
        <button className="modal-close" onClick={() => setDecision(null)} aria-label="Close request decision">×</button><div className="eyebrow">REQUEST REVIEW</div>
        <h2 id="request-decision-title">{decision === 'Accepted' ? 'Accept this request?' : 'Decline this request?'}</h2>
        <form className="create-project-form" onSubmit={(event) => { event.preventDefault(); void performAction('decision', decision) }}>
          <label>Response to client<textarea value={note} onChange={(event) => setNote(event.target.value)} maxLength={2000} required /></label>
          {error && <p className="payment-error" role="alert">{error}</p>}
          <div className="modal-actions"><button type="button" className="cancel-button" onClick={() => setDecision(null)} disabled={busy}>Cancel</button><button className="primary-button" disabled={busy || !note.trim()}>{busy ? 'Saving…' : `Confirm ${decision.toLowerCase()}`}</button></div>
        </form>
      </section>
    </div>}
  </div>

  return <div className="page-enter">
    <div className="page-heading"><div><div className="eyebrow">{admin ? 'CLIENT INTAKE' : 'PROJECT INTAKE'}</div><h1>{admin ? 'Requests inbox' : 'Your requests'}<span className="heading-period">.</span></h1><p>{admin ? 'Review incoming work from your clients.' : 'Submit new work and follow its progress.'}</p></div>
      {!admin && <button className="primary-button" onClick={() => navigate('/requests/new')}>New request</button>}
    </div>
    <div className="request-filters">
      <label>Status<select value={statusFilter} onChange={(event) => setStatusFilter(event.target.value)}><option value="">All statuses</option>{['New', 'InReview', 'Accepted', 'Declined', 'Converted', 'Cancelled'].map((value) => <option key={value} value={value}>{value === 'InReview' ? 'In review' : value}</option>)}</select></label>
      {admin && <label>Client<select value={clientFilter} onChange={(event) => setClientFilter(event.target.value)}><option value="">All clients</option>{clients.map((client) => <option key={client.id} value={client.id}>{client.companyName}</option>)}</select></label>}
    </div>
    <section className="panel full-panel request-list">
      {listQuery.isLoading ? <div className="approval-skeletons" role="status" aria-label="Loading requests"><i /><i /><i /></div> :
        listQuery.isError ? <div className="request-inline-error" role="alert">{listQuery.error instanceof Error ? listQuery.error.message : 'Unable to load requests.'}</div> :
          listQuery.data?.items.length ? listQuery.data.items.map((requestItem) => <button key={requestItem.id} className="request-list-item" onClick={() => navigate(`/requests/${requestItem.id}`)}>
            <span className="request-list-main"><strong>{requestItem.title}</strong><span>{admin ? requestItem.clientName : 'Submitted'} · {new Date(requestItem.createdAt).toLocaleDateString()}</span></span>
            <span className={`status-pill status-${requestItem.status.toLowerCase()}`}>{requestItem.status === 'InReview' ? 'In review' : requestItem.status}</span>
            <Icon name="arrow" />
          </button>) : <EmptyState title={admin ? 'No matching requests' : 'No requests yet'} text={admin ? 'Requests from clients will appear here.' : 'Submit a work request when you need something new from your agency.'} />}
    </section>
  </div>
}

function formatBytes(bytes: number) {
  if (bytes < 1024) return `${bytes} B`
  if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(0)} KB`
  return `${(bytes / (1024 * 1024)).toFixed(1)} MB`
}

function Invoices({ invoices, token, admin, clients, onChanged }: {
  invoices: Invoice[]; token: string; admin: boolean; clients: ClientSummary[]; onChanged: () => void
}) {
  const navigate = useNavigate()
  const invoiceId = useLocation().pathname.match(/^\/invoices\/([0-9a-f-]+)$/i)?.[1]
  const [busy, setBusy] = useState('')
  const [error, setError] = useState('')
  const [paymentInvoice, setPaymentInvoice] = useState<Invoice | null>(null)
  const [clientSecret, setClientSecret] = useState('')
  const [paymentComplete, setPaymentComplete] = useState(false)
  const [creating, setCreating] = useState(false)
  const [editingInvoice, setEditingInvoice] = useState<Invoice | null>(null)
  const [newInvoiceDueDate, setNewInvoiceDueDate] = useState('')
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
  async function updateDraft(invoice: Invoice, action: 'send' | 'delete') {
    setBusy(invoice.id); setError('')
    try {
      await request<void>(`/invoices/${invoice.id}${action === 'send' ? '/send' : ''}`, token, {
        method: action === 'send' ? 'POST' : 'DELETE',
      })
      onChanged()
    } catch (reason) { setError(reason instanceof Error ? reason.message : `Unable to ${action} invoice.`) }
    finally { setBusy('') }
  }
  async function exportCsv() {
    setError('')
    try {
      const response = await fetch(`${apiBase}/api/invoices/export.csv`, { headers: { Authorization: `Bearer ${token}` } })
      if (!response.ok) throw new Error(`Could not export invoices (${response.status}).`)
      const url = URL.createObjectURL(await response.blob())
      const anchor = document.createElement('a'); anchor.href = url; anchor.download = 'invoices.csv'; anchor.click()
      URL.revokeObjectURL(url)
    } catch (reason) { setError(reason instanceof Error ? reason.message : 'Unable to export invoices.') }
  }
  if (invoiceId) return <InvoiceDetailPage invoiceId={invoiceId} token={token} admin={admin} onBack={() => navigate('/invoices')} />
  return <div className="page-enter">
    <div className="page-heading"><div><div className="eyebrow">BILLING</div><h1>Invoices<span className="heading-period">.</span></h1><p>Everything related to your billing, all together.</p></div><div className="invoice-heading-actions"><span className="invoice-total">{invoices.length} total</span>{admin && <><button className="cancel-button" onClick={() => void exportCsv()}>Export CSV</button><button className="primary-button" onClick={() => { const dueDate = new Date(); dueDate.setDate(dueDate.getDate() + 30); setNewInvoiceDueDate(dueDate.toISOString().slice(0, 10)); setCreating(true) }}>＋ New invoice</button></>}</div></div>
    {error && <div className="alert" role="alert">{error}</div>}
    <section className="panel full-panel">
      <div className="invoice-table-head"><span>INVOICE</span><span>ISSUED</span><span>DUE DATE</span><span>STATUS</span><span>AMOUNT</span><span /></div>
      {invoices.length ? invoices.map((invoice) => <div className="invoice-table-row" key={invoice.id}>
        <div className="invoice-number"><span className="invoice-file"><Icon name="file" /></span><button className="invoice-detail-link" onClick={() => navigate(`/invoices/${invoice.id}`)}>{invoice.number}</button></div>
        <span>{new Date(invoice.issueDate).toLocaleDateString('en', { month: 'short', day: 'numeric', year: 'numeric' })}</span>
        <span>{new Date(invoice.dueDate).toLocaleDateString('en', { month: 'short', day: 'numeric', year: 'numeric' })}</span>
        <span className={`status-pill status-${invoice.status.toLowerCase()}`}>{invoice.status}</span>
        <strong>{new Intl.NumberFormat('en-US', { style: 'currency', currency: invoice.currency }).format(invoice.total)}</strong>
        <div className="invoice-actions">
          {admin && invoice.status === 'Draft' && <>
            <button className="text-button" onClick={() => setEditingInvoice(invoice)}>Edit</button>
            <button className="text-button" onClick={() => void updateDraft(invoice, 'send')} disabled={busy === invoice.id}>Send</button>
            <button className="cancel-button" onClick={() => void updateDraft(invoice, 'delete')} disabled={busy === invoice.id}>Delete</button>
          </>}
          {(invoice.status === 'Sent' || invoice.status === 'Overdue') && stripePromise && <button className="pay-button" onClick={() => void beginPayment(invoice)} disabled={busy === invoice.id}>{busy === invoice.id ? '...' : 'Pay'}</button>}
          <button className="download-button" onClick={() => void download(invoice)} disabled={busy === invoice.id}>{busy === invoice.id ? '...' : 'PDF'} <Icon name="arrow" /></button>
        </div>
      </div>) : <EmptyState title="No invoices yet" text="Any new invoice will be easy to find here." />}
    </section>
    {!stripePromise && invoices.some((invoice) => invoice.status === 'Sent' || invoice.status === 'Overdue') && <p className="payment-note">Online payments are not configured for this demo.</p>}
    {(creating || editingInvoice) && <InvoiceDraftModal invoice={editingInvoice} clients={clients} token={token}
      initialDueDate={newInvoiceDueDate}
      onClose={() => { setCreating(false); setEditingInvoice(null) }}
      onSaved={() => { setCreating(false); setEditingInvoice(null); onChanged() }} />}
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

function InvoiceDetailPage({ invoiceId, token, admin, onBack }: {
  invoiceId: string; token: string; admin: boolean; onBack: () => void
}) {
  const detailQuery = useQuery({
    queryKey: ['invoice-detail', token, invoiceId],
    queryFn: () => request<InvoiceDetails>(`/invoices/${invoiceId}`, token),
  })
  const [error, setError] = useState('')
  const [busy, setBusy] = useState(false)
  async function downloadReceipt() {
    setBusy(true); setError('')
    try {
      const response = await fetch(`${apiBase}/api/invoices/${invoiceId}/receipt.pdf`, {
        headers: { Authorization: `Bearer ${token}` },
      })
      if (!response.ok) throw new Error(`Receipt download failed (${response.status}).`)
      const url = URL.createObjectURL(await response.blob())
      const anchor = document.createElement('a'); anchor.href = url; anchor.download = `${detailQuery.data?.number ?? 'invoice'}-receipt.pdf`; anchor.click()
      URL.revokeObjectURL(url)
    } catch (reason) { setError(reason instanceof Error ? reason.message : 'Unable to download receipt.') }
    finally { setBusy(false) }
  }
  if (detailQuery.isLoading) return <div className="page-enter"><div className="page-heading"><div><div className="eyebrow">BILLING</div><h1>Invoice details<span className="heading-period">.</span></h1></div></div><section className="panel full-panel"><div className="loading-bar" role="status" aria-label="Loading invoice details" /></section></div>
  const invoice = detailQuery.data
  if (detailQuery.isError || !invoice) return <div className="page-enter"><div className="page-heading"><div><div className="eyebrow">BILLING</div><h1>Invoice not found<span className="heading-period">.</span></h1><p>This invoice is unavailable to your account.</p></div><button className="cancel-button" onClick={onBack}>Back to invoices</button></div><div className="alert" role="alert">{detailQuery.error instanceof Error ? detailQuery.error.message : 'Invoice not found.'}</div></div>
  return <div className="page-enter">
    <div className="page-heading"><div><div className="eyebrow">BILLING</div><h1>{invoice.number}<span className="heading-period">.</span></h1><p>Issued {new Date(invoice.issueDate).toLocaleDateString()} · Due {new Date(invoice.dueDate).toLocaleDateString()}</p></div><div className="invoice-heading-actions"><span className={`status-pill status-${invoice.status.toLowerCase()}`}>{invoice.status}</span><button className="cancel-button" onClick={onBack}>Back to invoices</button></div></div>
    {error && <div className="alert" role="alert">{error}</div>}
    <div className="invoice-detail-grid">
      <section className="panel invoice-detail-panel">
        <div className="panel-heading"><div><h2>Line items</h2><p>Itemized charges and totals.</p></div></div>
        <div className="invoice-detail-lines">{invoice.lines.map((line, index) => <div className="invoice-detail-line" key={`${line.description}-${index}`}><span><strong>{line.description}</strong><small>{line.quantity} × {new Intl.NumberFormat('en-US', { style: 'currency', currency: invoice.currency }).format(line.unitPrice)}</small></span><b>{new Intl.NumberFormat('en-US', { style: 'currency', currency: invoice.currency }).format(line.quantity * line.unitPrice)}</b></div>)}</div>
        <div className="invoice-detail-totals"><div><span>Subtotal</span><strong>{new Intl.NumberFormat('en-US', { style: 'currency', currency: invoice.currency }).format(invoice.lineSubtotal)}</strong></div><div><span>Discount</span><strong>−{new Intl.NumberFormat('en-US', { style: 'currency', currency: invoice.currency }).format(invoice.discountAmount)}</strong></div><div><span>Tax</span><strong>{new Intl.NumberFormat('en-US', { style: 'currency', currency: invoice.currency }).format(invoice.taxAmount)}</strong></div><div className="grand-total"><span>Total</span><strong>{new Intl.NumberFormat('en-US', { style: 'currency', currency: invoice.currency }).format(invoice.total)}</strong></div></div>
        {invoice.status === 'Paid' && !admin && <button className="primary-button receipt-button" onClick={() => void downloadReceipt()} disabled={busy}>{busy ? 'Preparing receipt…' : 'Download receipt PDF'}</button>}
      </section>
      <section className="panel invoice-detail-panel">
        <div className="panel-heading"><div><h2>Payment history</h2><p>Recorded payments for this invoice.</p></div></div>
        {invoice.payments.length ? invoice.payments.map((payment, index) => <div className="invoice-payment-row" key={`${payment.stripePaymentIntentId}-${index}`}><span><strong>{payment.status}</strong><small>{payment.paidAt ? new Date(payment.paidAt).toLocaleString() : 'Date unavailable'} · {payment.stripePaymentIntentId}</small></span><b>{new Intl.NumberFormat('en-US', { style: 'currency', currency: invoice.currency }).format(payment.amount)}</b></div>) : <EmptyState title="No payments recorded" text="Payments will appear here after they are confirmed." />}
      </section>
      <section className="panel invoice-detail-panel invoice-timeline-panel">
        <div className="panel-heading"><div><h2>Status timeline</h2><p>Invoice lifecycle updates.</p></div></div>
        {invoice.statusTimeline.map((event, index) => <div className="invoice-timeline-event" key={`${event.status}-${index}`}><span className="timeline-dot" /><span><strong>{event.status}</strong><small>{event.at ? new Date(event.at).toLocaleString() : 'Date unavailable'}</small></span></div>)}
      </section>
    </div>
  </div>
}

function InvoiceDraftModal({ invoice, clients, token, initialDueDate, onClose, onSaved }: {
  invoice: Invoice | null
  clients: ClientSummary[]
  token: string
  initialDueDate: string
  onClose: () => void
  onSaved: () => void
}) {
  const [clientId, setClientId] = useState(invoice?.clientId ?? clients[0]?.id ?? '')
  const [dueDate, setDueDate] = useState(invoice?.dueDate ?? initialDueDate)
  const [discountAmount, setDiscountAmount] = useState(invoice?.discountAmount ?? 0)
  const [taxAmount, setTaxAmount] = useState(invoice?.taxAmount ?? 0)
  const [lines, setLines] = useState(() => invoice?.lines.map((line) => ({ ...line })) ?? [{ description: '', quantity: 1, unitPrice: 0 }])
  const [error, setError] = useState('')
  const [busy, setBusy] = useState(false)

  function updateLine(index: number, patch: Partial<(typeof lines)[number]>) {
    setLines((current) => current.map((line, lineIndex) => lineIndex === index ? { ...line, ...patch } : line))
  }

  async function submit(event: FormEvent) {
    event.preventDefault()
    setBusy(true); setError('')
    try {
      await request(invoice ? `/invoices/${invoice.id}` : '/invoices', token, {
        method: invoice ? 'PUT' : 'POST',
        body: JSON.stringify({ clientId, projectId: null, dueDate, currency: 'USD', lines, discountAmount, taxAmount }),
      })
      onSaved()
    } catch (reason) { setError(reason instanceof Error ? reason.message : 'Unable to save the invoice.') }
    finally { setBusy(false) }
  }

  return <div className="modal-backdrop" role="presentation" onMouseDown={(event) => { if (event.target === event.currentTarget) onClose() }}>
    <section className="payment-modal project-modal invoice-draft-modal" role="dialog" aria-modal="true" aria-labelledby="invoice-draft-title">
      <button className="modal-close" onClick={onClose} aria-label="Close invoice form">×</button>
      <div className="eyebrow">BILLING</div><h2 id="invoice-draft-title">{invoice ? `Edit ${invoice.number}` : 'Create invoice draft'}</h2>
      <form className="create-project-form" onSubmit={submit}>
        <label>Client<select value={clientId} onChange={(event) => setClientId(event.target.value)} required>
          {clients.filter((client) => client.isActive || client.id === invoice?.clientId).map((client) => <option key={client.id} value={client.id}>{client.companyName}</option>)}
        </select></label>
        <label>Due date<input type="date" value={dueDate} onChange={(event) => setDueDate(event.target.value)} required /></label>
        <div className="date-fields"><label>Discount amount<input type="number" min="0" step="0.01" value={discountAmount} onChange={(event) => setDiscountAmount(Number(event.target.value))} /></label><label>Tax amount<input type="number" min="0" step="0.01" value={taxAmount} onChange={(event) => setTaxAmount(Number(event.target.value))} /></label></div>
        <div className="invoice-line-editor">
          {lines.map((line, index) => <div className="invoice-line-edit" key={index}>
            <label>Description<input value={line.description} onChange={(event) => updateLine(index, { description: event.target.value })} maxLength={240} required /></label>
            <label>Qty<input type="number" min="0.01" max="1000000" step="0.01" value={line.quantity} onChange={(event) => updateLine(index, { quantity: Number(event.target.value) })} required /></label>
            <label>Unit price<input type="number" min="0" max="1000000000" step="0.01" value={line.unitPrice} onChange={(event) => updateLine(index, { unitPrice: Number(event.target.value) })} required /></label>
            {lines.length > 1 && <button type="button" className="cancel-button" onClick={() => setLines((current) => current.filter((_, lineIndex) => lineIndex !== index))} aria-label={`Remove line ${index + 1}`}>×</button>}
          </div>)}
        </div>
        <button type="button" className="text-button" onClick={() => setLines((current) => [...current, { description: '', quantity: 1, unitPrice: 0 }])} disabled={lines.length >= 50}>＋ Add line item</button>
        {error && <p className="payment-error" role="alert">{error}</p>}
        {!clients.some((client) => client.isActive) && <p className="payment-error" role="alert">Create an active client before creating an invoice.</p>}
        <div className="modal-actions"><button type="button" className="cancel-button" onClick={onClose}>Cancel</button><button className="primary-button" disabled={busy || !clients.some((client) => client.isActive)}>{busy ? 'Saving...' : 'Save draft'}</button></div>
      </form>
    </section>
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

function Clients({ clients, token, onChanged }: { clients: ClientSummary[]; token: string; onChanged: () => void }) {
  const [search, setSearch] = useState('')
  const [newClientOpen, setNewClientOpen] = useState(false)
  const [inviteClient, setInviteClient] = useState<ClientSummary | null>(null)
  const [companyName, setCompanyName] = useState('')
  const [notes, setNotes] = useState('')
  const [inviteName, setInviteName] = useState('')
  const [inviteEmail, setInviteEmail] = useState('')
  const [selectedClientId, setSelectedClientId] = useState('')
  const [details, setDetails] = useState<ClientDetails | null>(null)
  const [detailsLoading, setDetailsLoading] = useState(false)
  const [editCompanyName, setEditCompanyName] = useState('')
  const [editNotes, setEditNotes] = useState('')
  const [busy, setBusy] = useState('')
  const [error, setError] = useState('')
  const filtered = clients.filter((client) => `${client.companyName} ${client.contact ?? ''}`.toLowerCase().includes(search.trim().toLowerCase()))

  async function createClient(event: FormEvent) {
    event.preventDefault()
    setBusy('create'); setError('')
    try {
      await request('/admin/clients', token, { method: 'POST', body: JSON.stringify({ companyName, notes }) })
      setCompanyName(''); setNotes(''); setNewClientOpen(false); onChanged()
    } catch (reason) { setError(reason instanceof Error ? reason.message : 'Unable to create the client.') }
    finally { setBusy('') }
  }

  async function inviteUser(event: FormEvent) {
    event.preventDefault()
    if (!inviteClient) return
    setBusy(inviteClient.id); setError('')
    try {
      await request(`/admin/clients/${inviteClient.id}/invitations`, token, {
        method: 'POST', body: JSON.stringify({ fullName: inviteName, email: inviteEmail }),
      })
      setInviteName(''); setInviteEmail(''); setInviteClient(null)
    } catch (reason) { setError(reason instanceof Error ? reason.message : 'Unable to send the invitation.') }
    finally { setBusy('') }
  }

  async function toggleClient(client: ClientSummary) {
    setBusy(client.id); setError('')
    try {
      await request(`/admin/clients/${client.id}/status`, token, {
        method: 'PATCH', body: JSON.stringify({ isActive: !client.isActive }),
      })
      onChanged()
    } catch (reason) { setError(reason instanceof Error ? reason.message : 'Unable to update client status.') }
    finally { setBusy('') }
  }

  async function showClient(client: ClientSummary) {
    if (selectedClientId === client.id) {
      setSelectedClientId('')
      setDetails(null)
      return
    }
    setSelectedClientId(client.id); setDetails(null); setDetailsLoading(true); setError('')
    try {
      const result = await request<ClientDetails>(`/admin/clients/${client.id}`, token)
      setDetails(result)
      setEditCompanyName(result.companyName)
      setEditNotes(result.notes)
    } catch (reason) { setError(reason instanceof Error ? reason.message : 'Unable to load client details.') }
    finally { setDetailsLoading(false) }
  }

  async function saveClientDetails(event: FormEvent) {
    event.preventDefault()
    if (!details) return
    setBusy(details.id); setError('')
    try {
      await request(`/admin/clients/${details.id}`, token, {
        method: 'PUT', body: JSON.stringify({ companyName: editCompanyName, notes: editNotes }),
      })
      const refreshed = await request<ClientDetails>(`/admin/clients/${details.id}`, token)
      setDetails(refreshed); onChanged()
    } catch (reason) { setError(reason instanceof Error ? reason.message : 'Unable to save client details.') }
    finally { setBusy('') }
  }

  async function toggleClientUser(user: ClientDetails['users'][number]) {
    if (!details) return
    setBusy(user.id); setError('')
    try {
      await request(`/admin/clients/${details.id}/users/${user.id}/status`, token, {
        method: 'PATCH', body: JSON.stringify({ isActive: !user.isActive }),
      })
      const refreshed = await request<ClientDetails>(`/admin/clients/${details.id}`, token)
      setDetails(refreshed); onChanged()
    } catch (reason) { setError(reason instanceof Error ? reason.message : 'Unable to update user status.') }
    finally { setBusy('') }
  }

  return <div className="page-enter">
    <div className="page-heading"><div><div className="eyebrow">RELATIONSHIPS</div><h1>Clients<span className="heading-period">.</span></h1><p>Good work starts with good relationships.</p></div><button className="primary-button" onClick={() => setNewClientOpen((open) => !open)}>＋ New client</button></div>
    {error && <div className="alert" role="alert">{error}<button onClick={() => setError('')} aria-label="Dismiss error">×</button></div>}
    {newClientOpen && <form className="panel admin-form" onSubmit={createClient}>
      <label>Company name<input value={companyName} onChange={(event) => setCompanyName(event.target.value)} maxLength={160} required /></label>
      <label>Notes<textarea value={notes} onChange={(event) => setNotes(event.target.value)} maxLength={4000} /></label>
      <div className="modal-actions"><button type="button" className="cancel-button" onClick={() => setNewClientOpen(false)}>Cancel</button><button className="primary-button" disabled={busy === 'create'}>{busy === 'create' ? 'Saving...' : 'Create client'}</button></div>
    </form>}
    <section className="panel full-panel">
      <div className="client-tools"><input aria-label="Search clients" placeholder="Search clients..." value={search} onChange={(event) => setSearch(event.target.value)} /><span>{filtered.length} clients</span></div>
      {filtered.length ? filtered.map((client) => <div className="client-record" key={client.id}>
        <div className="client-row">
          <div className="client-avatar">{client.companyName[0]}</div>
          <div className="client-details"><strong>{client.companyName}</strong><span>{client.contact ?? 'No active contact'} · {client.isActive ? 'Active' : 'Inactive'}</span></div>
          <span className="client-project-count">{client.activeProjects} active {client.activeProjects === 1 ? 'project' : 'projects'}</span>
          <button className="text-button" onClick={() => void showClient(client)}>{selectedClientId === client.id ? 'Hide details' : 'Details'}</button>
          <button className="text-button" onClick={() => setInviteClient(client)} disabled={!client.isActive}>Invite user</button>
          <button className="cancel-button" onClick={() => void toggleClient(client)} disabled={busy === client.id}>{client.isActive ? 'Deactivate' : 'Activate'}</button>
        </div>
        {selectedClientId === client.id && <div className="client-detail-card">
          {detailsLoading ? <p className="popover-hint">Loading client details...</p> : details && <>
            <div className="client-financials"><span>{details.projectCount} projects</span><span>{money(details.outstandingBalance)} outstanding</span><span>{money(details.lifetimeRevenue)} lifetime revenue</span></div>
            <form className="admin-form client-edit-form" onSubmit={saveClientDetails}>
              <label>Company name<input value={editCompanyName} onChange={(event) => setEditCompanyName(event.target.value)} maxLength={160} required /></label>
              <label>Notes<textarea value={editNotes} onChange={(event) => setEditNotes(event.target.value)} maxLength={4000} /></label>
              <button className="primary-button" disabled={busy === details.id}>{busy === details.id ? 'Saving...' : 'Save client'}</button>
            </form>
            <div className="client-user-list"><strong>Client users</strong>{details.users.length ? details.users.map((user) => <div className="client-user-row" key={user.id}>
              <span><b>{user.fullName}</b><small>{user.email} · {user.isActive ? 'Active' : 'Inactive'}</small></span>
              <button className="cancel-button" onClick={() => void toggleClientUser(user)} disabled={busy === user.id}>{user.isActive ? 'Deactivate' : 'Activate'}</button>
            </div>) : <p className="popover-hint">No client users yet.</p>}</div>
          </>}
        </div>}
      </div>) : <EmptyState title="No clients to show" text="Create a client or adjust your search." />}
    </section>
    {inviteClient && <div className="modal-backdrop" role="presentation" onMouseDown={(event) => { if (event.target === event.currentTarget) setInviteClient(null) }}>
      <section className="payment-modal project-modal" role="dialog" aria-modal="true" aria-labelledby="invite-title">
        <button className="modal-close" onClick={() => setInviteClient(null)} aria-label="Close invitation">×</button>
        <div className="eyebrow">CLIENT ACCESS</div><h2 id="invite-title">Invite to {inviteClient.companyName}</h2>
        <form className="create-project-form" onSubmit={inviteUser}>
          <label>Full name<input value={inviteName} onChange={(event) => setInviteName(event.target.value)} maxLength={120} required /></label>
          <label>Email<input type="email" value={inviteEmail} onChange={(event) => setInviteEmail(event.target.value)} maxLength={254} required /></label>
          {error && <p className="payment-error" role="alert">{error}</p>}
          <button className="primary-button" disabled={busy === inviteClient.id}>{busy === inviteClient.id ? 'Sending...' : 'Send invitation'}</button>
        </form>
      </section>
    </div>}
  </div>
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

function ProjectDetail({ project, comments, setComments, files, setFiles, token, currentUserId, isAdmin, commentText, setCommentText, onSubmit, onUpload, onDownload, onDownloadZip, onProjectChanged, onBack }: {
  project: Project; comments: Comment[]; setComments: React.Dispatch<React.SetStateAction<Comment[]>>
  files: ProjectFile[]; setFiles: React.Dispatch<React.SetStateAction<ProjectFile[]>>
  token: string; currentUserId: string; isAdmin: boolean; commentText: string; setCommentText: (value: string) => void
  onSubmit: (event: FormEvent) => void; onUpload: (file: File, onProgress: (progress: number) => void) => Promise<void>
  onDownload: (file: ProjectFile) => void; onDownloadZip: (projectId: string) => Promise<void>
  onProjectChanged: () => Promise<void>; onBack: () => void
}) {
  const [realtimeError, setRealtimeError] = useState('')
  const [actionError, setActionError] = useState('')
  const [editingComment, setEditingComment] = useState<string | null>(null)
  const [commentDraft, setCommentDraft] = useState('')
  const [editingFile, setEditingFile] = useState<string | null>(null)
  const [fileNameDraft, setFileNameDraft] = useState('')
  const [busyAction, setBusyAction] = useState(false)
  const [uploadStatuses, setUploadStatuses] = useState<{ id: string; name: string; progress: number; error?: string }[]>([])
  const [preview, setPreview] = useState<{ file: ProjectFile; url: string } | null>(null)

  useEffect(() => () => {
    if (preview) URL.revokeObjectURL(preview.url)
  }, [preview])

  async function uploadFiles(selectedFiles: File[]) {
    const newStatuses = selectedFiles.map((file) => ({ id: crypto.randomUUID(), name: file.name, progress: 0 }))
    setUploadStatuses((existing) => [...existing, ...newStatuses])
    await Promise.all(selectedFiles.map(async (file, index) => {
      const uploadId = newStatuses[index].id
      try {
        await onUpload(file, (progress) => setUploadStatuses((existing) => existing.map((status) =>
          status.id === uploadId ? { ...status, progress } : status)))
      } catch (reason) {
        setUploadStatuses((existing) => existing.map((status) => status.id === uploadId
          ? { ...status, error: reason instanceof Error ? reason.message : 'Upload failed.' }
          : status))
      }
    }))
  }

  async function openPreview(file: ProjectFile) {
    setActionError('')
    try {
      const response = await fetch(`${apiBase}/api/projects/${project.id}/files/${file.id}/preview`, {
        headers: { Authorization: `Bearer ${token}` },
      })
      if (!response.ok) {
        const body = await response.json().catch(() => null) as { message?: string } | null
        throw new Error(body?.message ?? `Preview failed (${response.status}).`)
      }
      setPreview({ file, url: URL.createObjectURL(await response.blob()) })
    } catch (reason) {
      setActionError(reason instanceof Error ? reason.message : 'Unable to preview this file.')
    }
  }

  async function requestApproval(milestoneId: string) {
    setBusyAction(true); setActionError('')
    try {
      await request(`/projects/${project.id}/milestones/${milestoneId}/approval-request`, token, { method: 'POST' })
      await onProjectChanged()
    } catch (reason) {
      setActionError(reason instanceof Error ? reason.message : 'Unable to request approval.')
    } finally { setBusyAction(false) }
  }

  async function saveComment(event: FormEvent, comment: Comment) {
    event.preventDefault()
    if (!commentDraft.trim()) return
    setBusyAction(true)
    setActionError('')
    try {
      await request(`/projects/${project.id}/comments/${comment.id}`, token, {
        method: 'PUT', body: JSON.stringify({ body: commentDraft.trim() }),
      })
      setComments((existing) => existing.map((item) => item.id === comment.id ? { ...item, body: commentDraft.trim() } : item))
      setEditingComment(null)
    } catch (reason) {
      setActionError(reason instanceof Error ? reason.message : 'Unable to edit this update.')
    } finally {
      setBusyAction(false)
    }
  }

  async function deleteComment(comment: Comment) {
    if (!window.confirm('Delete this project update?')) return
    setBusyAction(true)
    setActionError('')
    try {
      await request(`/projects/${project.id}/comments/${comment.id}`, token, { method: 'DELETE' })
      setComments((existing) => existing.filter((item) => item.id !== comment.id))
    } catch (reason) {
      setActionError(reason instanceof Error ? reason.message : 'Unable to delete this update.')
    } finally {
      setBusyAction(false)
    }
  }

  async function renameFile(event: FormEvent, file: ProjectFile) {
    event.preventDefault()
    if (!fileNameDraft.trim()) return
    setBusyAction(true)
    setActionError('')
    try {
      await request(`/projects/${project.id}/files/${file.id}`, token, {
        method: 'PATCH', body: JSON.stringify({ fileName: fileNameDraft.trim() }),
      })
      setFiles((existing) => existing.map((item) => item.id === file.id ? { ...item, fileName: fileNameDraft.trim() } : item))
      setEditingFile(null)
    } catch (reason) {
      setActionError(reason instanceof Error ? reason.message : 'Unable to rename this file.')
    } finally {
      setBusyAction(false)
    }
  }

  async function deleteFile(file: ProjectFile) {
    if (!window.confirm(`Delete "${file.fileName}"?`)) return
    setBusyAction(true)
    setActionError('')
    try {
      await request(`/projects/${project.id}/files/${file.id}`, token, { method: 'DELETE' })
      setFiles((existing) => existing.filter((item) => item.id !== file.id))
    } catch (reason) {
      setActionError(reason instanceof Error ? reason.message : 'Unable to delete this file.')
    } finally {
      setBusyAction(false)
    }
  }

  useEffect(() => {
    const connection = new HubConnectionBuilder()
      .withUrl(`${apiBase}/hubs/projects/${project.id}`, { accessTokenFactory: () => token })
      .withAutomaticReconnect()
      .build()
    connection.on('CommentAdded', (comment: Comment) => {
      setComments((existing) => existing.some((item) => item.id === comment.id) ? existing : [...existing, comment])
      setRealtimeError('')
    })
    connection.on('CommentUpdated', (comment: Comment) => {
      setComments((existing) => existing.map((item) => item.id === comment.id ? comment : item))
      setRealtimeError('')
    })
    connection.on('CommentDeleted', (event: { id: string }) => {
      setComments((existing) => existing.filter((item) => item.id !== event.id))
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
    {actionError && <div className="alert" role="alert">{actionError}<button onClick={() => setActionError('')} aria-label="Dismiss error">×</button></div>}
    <div className="detail-grid">
      <div className="detail-stack">
        <section className="panel detail-main">
          <div className="panel-heading"><div><h2>Milestones</h2><p>Your progress, step by step.</p></div><span className="completion-label">{project.progress}% complete</span></div>
          <div className="detail-progress"><i style={{ width: `${project.progress}%` }} /></div>
          <div className="milestone-list">{project.milestones.map((milestone, index) => {
            const approvalStatus = milestone.approvalStatus ?? 'NotRequired'
            return <div className="milestone" key={milestone.id}>
              <div className={`milestone-check ${milestone.isCompleted ? 'done' : ''}`}>{milestone.isCompleted ? '✓' : String(index + 1).padStart(2, '0')}</div>
              <div className="milestone-copy"><strong>{milestone.title}</strong><span>{milestone.isCompleted ? 'Completed' : milestone.dueDate ? `Due ${new Date(milestone.dueDate).toLocaleDateString('en', { month: 'short', day: 'numeric' })}` : 'Up next'}</span>
                {approvalStatus !== 'NotRequired' && <span className={`approval-status approval-${approvalStatus.toLowerCase()}`}>{approvalStatus === 'Pending' ? 'Waiting on client' : approvalStatus === 'ChangesRequested' ? `Changes requested${milestone.decisionNote ? `: ${milestone.decisionNote}` : ''}` : 'Approved'}</span>}
              </div>
              {milestone.isCompleted && <span className="done-tag">DONE</span>}
              {isAdmin && !milestone.isCompleted && approvalStatus !== 'Pending' && <button className="download-button" onClick={() => void requestApproval(milestone.id)} disabled={busyAction}>Request approval</button>}
            </div>
          })}</div>
          <div className="project-dates"><span>Started <b>{new Date(project.startDate).toLocaleDateString('en', { month: 'short', day: 'numeric', year: 'numeric' })}</b></span><span>Due <b>{project.dueDate ? new Date(project.dueDate).toLocaleDateString('en', { month: 'short', day: 'numeric', year: 'numeric' }) : 'Not set'}</b></span></div>
        </section>
        <section className="panel files-panel">
          <div className="panel-heading"><div><h2>Project files</h2><p>Shared files for this project.</p></div><div className="file-header-actions"><button className="download-button" onClick={() => {
            setBusyAction(true); setActionError('')
            void onDownloadZip(project.id).catch((reason: unknown) => setActionError(reason instanceof Error ? reason.message : 'Unable to download the project archive.')).finally(() => setBusyAction(false))
          }} disabled={busyAction || files.length === 0}>Download ZIP</button><label className="upload-button"><Icon name="upload" /> Upload<input type="file" multiple accept=".pdf,.png,.jpg,.jpeg,.txt" onChange={(event) => { const selected = Array.from(event.target.files ?? []); if (selected.length) void uploadFiles(selected); event.currentTarget.value = '' }} /></label></div></div>
          <div className="file-drop-zone" onDragOver={(event) => event.preventDefault()} onDrop={(event) => { event.preventDefault(); const dropped = Array.from(event.dataTransfer.files); if (dropped.length) void uploadFiles(dropped) }}>Drop files here or use Upload. PDF, PNG, JPEG, and TXT up to 10 MB.</div>
          {uploadStatuses.length > 0 && <div className="file-upload-statuses" aria-live="polite">{uploadStatuses.map((status) => <div className={`file-upload-status ${status.error ? 'failed' : ''}`} key={status.id}><span>{status.name}</span>{status.error ? <strong role="alert">{status.error}</strong> : <><progress max="100" value={status.progress} aria-label={`Uploading ${status.name}`} /><small>{status.progress}%</small></>}</div>)}</div>}
          {files.length ? <div className="file-list">{(['Admin', 'Client'] as const).map((role) => {
            const groupedFiles = files.filter((file) => (file.uploadedByRole === 'Client' ? 'Client' : 'Admin') === role)
            if (!groupedFiles.length) return null
            return <div className="file-group" key={role}><h3>{role === 'Client' ? 'Uploaded by client' : 'Uploaded by agency'}</h3>{groupedFiles.map((file) => <div className="file-row" key={file.id}>
            <span className="invoice-file"><Icon name="file" /></span>
            {editingFile === file.id ? <form className="inline-edit-form" onSubmit={(event) => void renameFile(event, file)}>
              <label className="sr-only" htmlFor={`file-name-${file.id}`}>File name</label>
              <input id={`file-name-${file.id}`} value={fileNameDraft} onChange={(event) => setFileNameDraft(event.target.value)} maxLength={255} required />
              <button className="download-button" type="submit" disabled={busyAction}>Save</button>
              <button className="cancel-button" type="button" onClick={() => setEditingFile(null)} disabled={busyAction}>Cancel</button>
            </form> : <span className="file-meta"><strong>{file.fileName}</strong><small>{(file.size / 1024).toFixed(0)} KB · {relativeDate(file.uploadedAt)}</small></span>}
            {['image/png', 'image/jpeg', 'application/pdf'].includes(file.contentType) && <button className="download-button" onClick={() => void openPreview(file)}>Preview</button>}
            <button className="download-button" onClick={() => onDownload(file)}>Download <Icon name="arrow" /></button>
            {(isAdmin || file.uploadedById === currentUserId) && editingFile !== file.id && <>
              <button className="download-button" onClick={() => { setEditingFile(file.id); setFileNameDraft(file.fileName); setActionError('') }}>Rename</button>
              <button className="cancel-button" onClick={() => void deleteFile(file)} disabled={busyAction}>Delete</button>
            </>}
          </div>)}</div>
          })}</div> : <EmptyState title="No files shared yet" text="Upload a file to keep everything in one place." />}
        </section>
      </div>
      <section className="panel comments-panel">
        <div className="panel-heading"><div><h2>Conversation</h2><p>Keep project updates in one place.</p></div><span className="conversation-count">{comments.length}</span></div>
        <div className="comments-list" aria-live="polite" aria-label="Project updates">{comments.length ? comments.map((comment) => <article className="comment" key={comment.id}>
          <div className="comment-avatar">{comment.author.split(' ').map((part) => part[0]).join('').slice(0, 2)}</div>
          <div className="comment-body">
            <div><strong>{comment.author}</strong><time>{relativeDate(comment.createdAt)}</time></div>
            {editingComment === comment.id ? <form className="inline-edit-form comment-edit-form" onSubmit={(event) => void saveComment(event, comment)}>
              <label className="sr-only" htmlFor={`comment-edit-${comment.id}`}>Edit project update</label>
              <textarea id={`comment-edit-${comment.id}`} value={commentDraft} onChange={(event) => setCommentDraft(event.target.value)} maxLength={5000} required />
              <button className="download-button" type="submit" disabled={busyAction}>Save</button>
              <button className="cancel-button" type="button" onClick={() => setEditingComment(null)} disabled={busyAction}>Cancel</button>
            </form> : <p>{comment.body}</p>}
            {(isAdmin || comment.authorId === currentUserId) && editingComment !== comment.id && <div className="comment-actions">
              <button className="download-button" onClick={() => { setEditingComment(comment.id); setCommentDraft(comment.body); setActionError('') }}>Edit</button>
              <button className="cancel-button" onClick={() => void deleteComment(comment)} disabled={busyAction}>Delete</button>
            </div>}
          </div>
        </article>) : <EmptyState title="Start the conversation" text="Share a question or update with your team." />}</div>
        <form className="comment-form" onSubmit={onSubmit}><label className="sr-only" htmlFor="comment">Write an update</label><textarea id="comment" value={commentText} onChange={(event) => setCommentText(event.target.value)} placeholder="Write an update..." maxLength={5000} /><button className="primary-button" disabled={!commentText.trim()}>Send update <Icon name="arrow" /></button></form>
      </section>
    </div>
    {preview && <div className="modal-backdrop" role="presentation" onMouseDown={(event) => { if (event.target === event.currentTarget) setPreview(null) }}>
      <section className="payment-modal file-preview-dialog" role="dialog" aria-modal="true" aria-labelledby="file-preview-title">
        <button className="modal-close" onClick={() => setPreview(null)} aria-label="Close file preview">×</button>
        <div className="eyebrow">PROJECT FILE</div><h2 id="file-preview-title">{preview.file.fileName}</h2>
        {preview.file.contentType === 'application/pdf'
          ? <iframe title={`Preview of ${preview.file.fileName}`} src={preview.url} />
          : <img src={preview.url} alt={preview.file.fileName} />}
      </section>
    </div>}
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
