import { afterEach, expect, it, vi } from 'vitest'
import { defineComponent } from 'vue'
import { mount } from '@vue/test-utils'
import { useConfigurationCommit } from '../../src/features/capabilities/configuration-commit'
import { beginOperation, clearPendingOperations, markOperation } from '../../src/shared/sync/pending-operations'
import type { ZbaClient, ZbaConfigurationSaveRequest } from '../../src/api/zba'
const mounted: ReturnType<typeof mount>[]=[]
afterEach(()=>{mounted.splice(0).forEach(w=>w.unmount());clearPendingOperations()})
it.each(['unknown','network'] as const)('a late %s check cannot relock another observer after terminal settlement',async(kind)=>{
 let finish!: (value: unknown)=>void
 const held=new Promise((resolve)=>{finish=resolve})
 let count=0
 const client={identityLabel:'synthetic-shared',baseUrl:'/zba/v1',findConfiguration:vi.fn(async()=>{if(++count===2){await held;if(kind==='network')throw Error('synthetic');return {code:12,errors:[]}}return {code:0,receipt:{key:'original',decision:0,commitState:1,applyState:3},errors:[]}})} as unknown as ZbaClient
 beginOperation(client,{key:'original',kind:'configuration',target:'shared'});markOperation(client,'original','unknown')
 const observe=()=>{let commit!: ReturnType<typeof useConfigurationCommit>;const w=mount(defineComponent({setup(){commit=useConfigurationCommit({client:()=>client,target:()=> 'shared',canWrite:()=>true,canQuery:()=>true,onAuth:()=>{}});return()=>null}}));mounted.push(w);return commit}
 const a=observe(),b=observe();const first=a.check();const second=b.check();expect(await first).toBe(true);finish(null);await second;expect(b.pendingKey.value).toBeNull();expect(b.phase.value).toBe('reload-required');b.acknowledgeReload();expect(b.phase.value).toBe('idle')
})
it('an old successful check cannot erase a newer pending save',async()=>{
 let resolveOld!: (value: unknown)=>void
 const old=new Promise(resolve=>{resolveOld=resolve});let calls=0
 const client={identityLabel:'synthetic-positive',baseUrl:'/zba/v1',findConfiguration:async()=>{if(++calls===2)await old;return {code:0,receipt:{key:'original',decision:0,commitState:1,applyState:3}}},saveConfiguration:async()=>new Promise(()=>{})} as unknown as ZbaClient
 beginOperation(client,{key:'original',kind:'configuration',target:'shared'});markOperation(client,'original','unknown')
 const observe=()=>{let commit!:ReturnType<typeof useConfigurationCommit>;mounted.push(mount(defineComponent({setup(){commit=useConfigurationCommit({client:()=>client,target:()=> 'shared',canWrite:()=>true,canQuery:()=>true,onAuth:()=>{}});return()=>null}})));return commit}
 const a=observe(),b=observe();const first=a.check(),second=b.check();await first;b.acknowledgeReload();void b.save('0123456789abcdef0123456789abcdef',{contractRevision:'cfg-0.3.3',expectedVersion:'2',changes:{},secrets:[]} as unknown as Omit<ZbaConfigurationSaveRequest,'key'>);const key=b.pendingKey.value;expect(key).not.toBe('original');resolveOld(null);await second;expect(b.pendingKey.value).toBe(key);expect(b.phase.value).toBe('saving')
})
