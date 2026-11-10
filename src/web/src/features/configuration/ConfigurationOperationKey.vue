<script setup lang="ts">
import { useId } from 'vue'

defineProps<{ operationKey: string }>()
const id = useId()

function selectKey(event: Event): void {
  if (event.target instanceof HTMLInputElement) event.target.select()
}
</script>

<template>
  <div class="inline-notice operation-key" data-testid="configuration-operation-key-notice">
    <label :for="id + '-key'">本次配置操作键</label>
    <input :id="id + '-key'" :value="operationKey" data-testid="configuration-operation-key" type="text"
      readonly autocomplete="off" autocapitalize="off" :spellcheck="false" :aria-describedby="id + '-help'"
      @focus="selectKey" @click="selectKey">
    <p :id="id + '-help'">选中原键后可复制留存。刷新或关闭页面前请自行保存；重新连接原服务并使用原身份进入管理员模式后，可在基础配置的“核对先前的配置保存”中查询原回执。</p>
  </div>
</template>

<style scoped>
.operation-key { display: grid; gap: 0.5rem; min-width: 0; }
.operation-key input { width: 100%; min-width: 0; box-sizing: border-box; font-family: ui-monospace, SFMono-Regular, Consolas, monospace; }
.operation-key p { margin: 0; overflow-wrap: anywhere; }
</style>
