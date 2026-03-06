import { DashboardContent } from "@/components/features/dashboard/dashboard-content";

export default async function LivePage({
  params,
}: {
  params: Promise<{ strategy: string }>;
}) {
  const { strategy } = await params;
  return <DashboardContent strategy={strategy} mode="live" />;
}
