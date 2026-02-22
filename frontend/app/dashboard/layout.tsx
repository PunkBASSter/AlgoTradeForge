// T056 - Dashboard layout with sidebar structure

export default function DashboardLayout({
  children,
}: {
  children: React.ReactNode;
}) {
  return <div className="min-h-[calc(100vh-100px)]">{children}</div>;
}
