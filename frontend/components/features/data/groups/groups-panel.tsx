"use client";

import { useState } from "react";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { dataApi } from "@/lib/services/data-api";
import { GroupCard } from "./group-card";
import { GroupEditor } from "./group-editor";
import { Button } from "@/components/ui/button";
import { SlideOver } from "@/components/ui/slide-over";

export function GroupsPanel() {
  const [editorOpen, setEditorOpen] = useState(false);
  const [editingGroup, setEditingGroup] = useState<string | null>(null);
  const queryClient = useQueryClient();

  const groupsQuery = useQuery({
    queryKey: ["data", "groups"],
    queryFn: ({ signal }) => dataApi.getGroups(signal),
  });

  const desiredStateQuery = useQuery({
    queryKey: ["data", "desired-state"],
    queryFn: ({ signal }) => dataApi.getDesiredState(undefined, signal),
  });

  function openCreate() {
    setEditingGroup(null);
    setEditorOpen(true);
  }

  function openEdit(groupName: string) {
    setEditingGroup(groupName);
    setEditorOpen(true);
  }

  function handleSaved() {
    setEditorOpen(false);
    // Invalidation is handled by GroupEditor; refetch desired-state too.
    void queryClient.invalidateQueries({ queryKey: ["data", "desired-state"] });
  }

  async function handleDelete(groupName: string) {
    await dataApi.deleteGroup(groupName);
    await queryClient.invalidateQueries({ queryKey: ["data", "groups"] });
    await queryClient.invalidateQueries({ queryKey: ["data", "desired-state"] });
  }

  return (
    <div className="p-6 space-y-4">
      <div className="flex items-center justify-between">
        <h2 className="text-lg font-semibold text-text-primary">Collection Groups</h2>
        <Button onClick={openCreate}>Create Group</Button>
      </div>

      {groupsQuery.isLoading && (
        <div className="text-text-secondary text-sm">Loading groups…</div>
      )}

      {groupsQuery.error && (
        <div className="text-accent-red text-sm">
          Failed to load groups:{" "}
          {groupsQuery.error instanceof Error
            ? groupsQuery.error.message
            : String(groupsQuery.error)}
        </div>
      )}

      {groupsQuery.data && groupsQuery.data.groups.length === 0 && (
        <div className="text-text-secondary text-sm">
          No groups configured. Create one to get started.
        </div>
      )}

      <div className="grid gap-3">
        {groupsQuery.data?.groups.map((group) => (
          <GroupCard
            key={group.name}
            summary={group}
            desiredState={desiredStateQuery.data}
            onEdit={() => openEdit(group.name)}
            onDelete={() => handleDelete(group.name)}
          />
        ))}
      </div>

      <SlideOver
        open={editorOpen}
        onClose={() => setEditorOpen(false)}
        title={editingGroup === null ? "Create Group" : `Edit: ${editingGroup}`}
      >
        {editorOpen && (
          <GroupEditor
            mode={editingGroup === null ? "create" : "edit"}
            name={editingGroup ?? undefined}
            onClose={() => setEditorOpen(false)}
            onSaved={handleSaved}
          />
        )}
      </SlideOver>
    </div>
  );
}
