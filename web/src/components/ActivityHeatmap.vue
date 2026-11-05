<script setup lang="ts">
import { computed, ref } from 'vue'
import type { ActivityHeatmapReport } from '@/services/api'
import { activityGrid, type HeatmapCell } from '@/services/activityHeatmap'

const props = defineProps<{ report: ActivityHeatmapReport }>()
const grid = computed(() => activityGrid(props.report.fromDay, props.report.toDay, props.report.days))
const selected = ref<HeatmapCell>()
const description = (cell: HeatmapCell) => `${cell.day} · ${cell.requests.toLocaleString('zh-CN')} 次请求`
function select(event: Event) {
  const day = (event.target as HTMLElement).dataset.day
  if (day) selected.value = grid.value.weeks.flat().find(cell => cell.day === day)
}
</script>

<template>
  <figure class="activity-heatmap">
    <figcaption class="sr-only">近一年请求活跃度，{{ report.activeDays }} 个活跃日，共 {{ report.requests }} 次请求</figcaption>
    <div class="activity-scroll">
      <div class="activity-plot" :style="{ '--weeks': grid.weeks.length }">
        <div class="activity-months" aria-hidden="true"><span v-for="(month, index) in grid.months" :key="index">{{ month }}</span></div>
        <div class="activity-weekdays" aria-hidden="true"><span v-for="(label, index) in ['一', '', '三', '', '五', '', '日']" :key="index">{{ label }}</span></div>
        <div class="activity-grid" @pointerover="select" @focusin="select" @click="select">
          <div v-for="(week, index) in grid.weeks" :key="index" class="activity-week">
            <template v-for="cell in week" :key="cell.day">
              <button v-if="cell.inRange" type="button" class="activity-cell" :data-day="cell.day" :data-level="cell.level"
                :aria-label="description(cell)" :aria-pressed="selected?.day === cell.day" :title="description(cell)" />
              <span v-else class="activity-cell activity-spacer" aria-hidden="true" />
            </template>
          </div>
        </div>
      </div>
    </div>
    <div class="activity-foot">
      <span aria-live="polite">{{ selected ? description(selected) : `${report.fromDay} — ${report.toDay} · ${report.activeDays} 个活跃日 · ${report.requests.toLocaleString('zh-CN')} 次请求` }}</span>
      <span class="activity-legend">少 <i v-for="level in [0, 1, 2, 3, 4]" :key="level" class="activity-cell" :data-level="level" /> 多</span>
    </div>
    <p class="metric-note">基于已保留日志 · 当前保留 {{ report.retentionDays }} 天{{ report.earliestLogUtc ? ` · 最早记录 ${new Date(report.earliestLogUtc).toLocaleDateString('zh-CN')}` : ' · 暂无已完成请求' }}</p>
  </figure>
</template>
