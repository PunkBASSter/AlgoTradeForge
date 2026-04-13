// T056 - Server component route for optimization group report page

import { OptimizationGroupPage } from "@/components/features/report/optimization-group-page";

export default async function OptimizationGroupReportPage({
  params,
}: {
  params: Promise<{ groupId: string }>;
}) {
  const { groupId } = await params;
  return <OptimizationGroupPage groupId={groupId} />;
}
