import { Bar, BarChart, CartesianGrid, Cell, Pie, PieChart, ResponsiveContainer, Tooltip, XAxis, YAxis } from 'recharts'

type AdminMetrics = {
  projectStatus: { status: string; count: number }[]
  revenueByMonth: { month: string; revenue: number }[]
}

const colors = ['#7b7dd7', '#8ebca0', '#e1ae76', '#7da8c7', '#b6a2cc']

function money(value: number) {
  return new Intl.NumberFormat('en-US', { style: 'currency', currency: 'USD', maximumFractionDigits: 0 }).format(value)
}

export default function AdminCharts({ metrics }: { metrics: AdminMetrics }) {
  return <div className="charts-grid">
    <section className="panel chart-panel">
      <div className="panel-heading"><div><h2>Revenue over time</h2><p>Verified payments across the last six months.</p></div></div>
      <div className="chart-area"><ResponsiveContainer width="100%" height="100%"><BarChart data={metrics.revenueByMonth} margin={{ top: 10, right: 6, left: -18, bottom: 0 }}>
        <CartesianGrid vertical={false} stroke="#eff0f4" strokeDasharray="3 4" />
        <XAxis dataKey="month" axisLine={false} tickLine={false} tick={{ fill: '#999daa', fontSize: 9 }} />
        <YAxis axisLine={false} tickLine={false} tick={{ fill: '#a1a4ae', fontSize: 8 }} tickFormatter={(value: number) => value === 0 ? '$0' : `$${value / 1000}k`} />
        <Tooltip cursor={{ fill: '#f5f5fb' }} formatter={(value) => [money(Number(value)), 'Revenue']} contentStyle={{ border: '1px solid #ececf3', borderRadius: 7, fontSize: 10 }} />
        <Bar dataKey="revenue" fill="#8284dc" radius={[4, 4, 0, 0]} maxBarSize={34} />
      </BarChart></ResponsiveContainer></div>
    </section>
    <section className="panel chart-panel">
      <div className="panel-heading"><div><h2>Projects by status</h2><p>A clear snapshot of work in progress.</p></div></div>
      <div className="status-chart">
        <div className="pie-area"><ResponsiveContainer width="100%" height="100%"><PieChart><Pie data={metrics.projectStatus} dataKey="count" nameKey="status" innerRadius={45} outerRadius={64} paddingAngle={3} stroke="none">{metrics.projectStatus.map((entry, index) => <Cell key={entry.status} fill={colors[index % colors.length]} />)}</Pie><Tooltip contentStyle={{ border: '1px solid #ececf3', borderRadius: 7, fontSize: 10 }} /></PieChart></ResponsiveContainer></div>
        <div className="chart-legend">{metrics.projectStatus.map((entry, index) => <div key={entry.status}><i style={{ backgroundColor: colors[index % colors.length] }} /><span>{entry.status.replace(/([A-Z])/g, ' $1').trim()}</span><strong>{entry.count}</strong></div>)}</div>
      </div>
    </section>
  </div>
}
