import { ValidationGroupPage } from "@/components/features/report/validation-group-page";

export default async function ValidationGroupReportPage({
  params,
}: {
  params: Promise<{ groupId: string }>;
}) {
  const { groupId } = await params;
  return <ValidationGroupPage groupId={groupId} />;
}
