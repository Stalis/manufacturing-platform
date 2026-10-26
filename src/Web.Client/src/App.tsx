import React, { useState } from 'react';
import { useQuery, useMutation, useSubscription } from '@apollo/client';
import {
  Factory,
  Package,
  Layers,
  Activity,
  Plus,
  Trash2,
  CheckCircle2,
  AlertTriangle,
  Clock,
  Loader2,
  Boxes,
  Radio,
} from 'lucide-react';
import {
  GET_DASHBOARD_DATA,
  CREATE_ORDER,
  ON_ORDER_UPDATED,
  ON_PRODUCER_EVENT,
} from './graphql/operations';
import type { Order, Product, Material, ProducerEvent, OrderStatus } from './types';

interface DashboardQueryData {
  orders: Order[];
  products: Product[];
  materials: Material[];
}

const STATUS_BADGE: Record<
  OrderStatus,
  { label: string; classes: string; icon: React.ReactNode }
> = {
  CREATED: {
    label: 'Created',
    classes: 'bg-slate-800 text-slate-300 border-slate-700',
    icon: <Clock className="w-3.5 h-3.5" />,
  },
  WAITING_FOR_RESERVE: {
    label: 'Waiting Reserve',
    classes: 'bg-amber-950/60 text-amber-300 border-amber-700/60',
    icon: <Loader2 className="w-3.5 h-3.5 animate-spin" />,
  },
  RESERVED: {
    label: 'Reserved',
    classes: 'bg-indigo-950/60 text-indigo-300 border-indigo-700/60',
    icon: <Boxes className="w-3.5 h-3.5" />,
  },
  IN_PROGRESS: {
    label: 'In Progress',
    classes: 'bg-sky-950/60 text-sky-300 border-sky-600/60',
    icon: <Loader2 className="w-3.5 h-3.5 animate-spin" />,
  },
  DONE: {
    label: 'Done',
    classes: 'bg-emerald-950/60 text-emerald-300 border-emerald-700/60',
    icon: <CheckCircle2 className="w-3.5 h-3.5" />,
  },
  ERROR: {
    label: 'Error',
    classes: 'bg-rose-950/60 text-rose-300 border-rose-700/60',
    icon: <AlertTriangle className="w-3.5 h-3.5" />,
  },
};

export function App() {
  const [activeTab, setActiveTab] = useState<'orders' | 'catalog'>('orders');
  const [draftItems, setDraftItems] = useState<
    Array<{ productId: string; revision: number; quantity: number }>
  >([]);
  const [eventFeed, setEventFeed] = useState<
    Array<ProducerEvent & { timestamp: string }>
  >([]);

  const { data, loading, error, refetch } =
    useQuery<DashboardQueryData>(GET_DASHBOARD_DATA);

  const [createOrder, { loading: creatingOrder, error: createError }] =
    useMutation(CREATE_ORDER, {
      onCompleted: () => {
        setDraftItems([]);
        refetch();
      },
    });

  useSubscription(ON_ORDER_UPDATED);

  useSubscription<{ onProducerEvent: ProducerEvent }>(ON_PRODUCER_EVENT, {
    onData: ({ data: subData }) => {
      const evt = subData.data?.onProducerEvent;
      if (!evt) return;
      setEventFeed((prev) => [
        { ...evt, timestamp: new Date().toLocaleTimeString() },
        ...prev.slice(0, 29),
      ]);
    },
  });

  const orders = data?.orders ?? [];
  const products = data?.products ?? [];
  const materials = data?.materials ?? [];

  const handleAddDraftLine = () => {
    const firstProduct = products[0];
    if (!firstProduct) return;
    setDraftItems((prev) => [
      ...prev,
      { productId: firstProduct.id, revision: 1, quantity: 2 },
    ]);
  };

  const handleSubmitOrder = async (e: React.FormEvent) => {
    e.preventDefault();
    if (draftItems.length === 0) return;
    await createOrder({
      variables: {
        input: {
          items: draftItems,
        },
      },
    });
  };

  return (
    <div className="min-h-screen bg-slate-950 text-slate-100 flex flex-col">
      {/* Header */}
      <header className="border-b border-slate-800 bg-slate-900/60 backdrop-blur sticky top-0 z-10">
        <div className="max-w-7xl mx-auto px-6 py-4 flex items-center justify-between">
          <div className="flex items-center gap-3">
            <div className="p-2 rounded-lg bg-sky-500/10 border border-sky-500/30 text-sky-400">
              <Factory className="w-6 h-6" />
            </div>
            <div>
              <h1 className="text-lg font-semibold tracking-tight">
                Manufacturing Platform
              </h1>
              <p className="text-xs text-slate-400">
                GraphQL BFF Gateway • Real-Time NATS JetStream Telemetry
              </p>
            </div>
          </div>

          <nav className="flex items-center gap-2 bg-slate-900 p-1 rounded-lg border border-slate-800">
            <button
              onClick={() => setActiveTab('orders')}
              className={`px-3.5 py-1.5 rounded-md text-sm font-medium transition flex items-center gap-2 ${
                activeTab === 'orders'
                  ? 'bg-sky-600 text-white shadow'
                  : 'text-slate-400 hover:text-slate-200'
              }`}
            >
              <Activity className="w-4 h-4" />
              Orders & Live Progress
              <span className="text-xs px-1.5 py-0.5 rounded bg-slate-950/40">
                {orders.length}
              </span>
            </button>
            <button
              onClick={() => setActiveTab('catalog')}
              className={`px-3.5 py-1.5 rounded-md text-sm font-medium transition flex items-center gap-2 ${
                activeTab === 'catalog'
                  ? 'bg-sky-600 text-white shadow'
                  : 'text-slate-400 hover:text-slate-200'
              }`}
            >
              <Layers className="w-4 h-4" />
              Catalog & BOMs
              <span className="text-xs px-1.5 py-0.5 rounded bg-slate-950/40">
                {products.length}
              </span>
            </button>
          </nav>
        </div>
      </header>

      {/* Main Content */}
      <main className="max-w-7xl w-full mx-auto px-6 py-8 flex-1">
        {error && (
          <div className="mb-6 p-4 rounded-xl bg-rose-950/50 border border-rose-800 text-rose-200 text-sm flex items-center justify-between">
            <span>GraphQL Gateway Error: {error.message}</span>
            <button
              onClick={() => refetch()}
              className="px-3 py-1 rounded bg-rose-800/60 hover:bg-rose-800 text-xs font-medium"
            >
              Retry
            </button>
          </div>
        )}

        {activeTab === 'orders' ? (
          <div className="grid grid-cols-1 lg:grid-cols-3 gap-6">
            {/* Left 2 columns: Order Creation + Active Orders */}
            <div className="lg:col-span-2 space-y-6">
              {/* New Order Card */}
              <section className="rounded-xl border border-slate-800 bg-slate-900/50 p-5">
                <div className="flex items-center justify-between mb-4">
                  <div>
                    <h2 className="text-base font-semibold flex items-center gap-2">
                      <Package className="w-4 h-4 text-sky-400" />
                      Dispatch Production Order
                    </h2>
                    <p className="text-xs text-slate-400">
                      Reserves BOM materials in Inventory and dispatches unit-level tasks to Producer workers
                    </p>
                  </div>
                  <button
                    type="button"
                    onClick={handleAddDraftLine}
                    disabled={products.length === 0}
                    className="px-3 py-1.5 rounded-lg bg-slate-800 hover:bg-slate-700 disabled:opacity-40 text-xs font-medium flex items-center gap-1.5 border border-slate-700 transition"
                  >
                    <Plus className="w-3.5 h-3.5" />
                    Add Product Line
                  </button>
                </div>

                {draftItems.length === 0 ? (
                  <div className="text-center py-6 border border-dashed border-slate-800 rounded-lg text-slate-500 text-sm">
                    {products.length === 0
                      ? 'No products in Catalog yet. Seed the catalog first (`dotnet run --project tools/Catalog.Seed`).'
                      : 'Click "Add Product Line" to build a new manufacturing order.'}
                  </div>
                ) : (
                  <form onSubmit={handleSubmitOrder} className="space-y-3">
                    {draftItems.map((line, idx) => (
                      <div
                        key={idx}
                        className="grid grid-cols-12 gap-3 items-center bg-slate-950/60 p-3 rounded-lg border border-slate-800"
                      >
                        <div className="col-span-6">
                          <label className="block text-[11px] text-slate-400 mb-1">
                            Product
                          </label>
                          <select
                            value={line.productId}
                            onChange={(e) => {
                              const val = e.target.value;
                              setDraftItems((prev) =>
                                prev.map((item, i) =>
                                  i === idx ? { ...item, productId: val } : item
                                )
                              );
                            }}
                            className="w-full bg-slate-900 border border-slate-700 rounded-md px-2.5 py-1.5 text-sm"
                          >
                            {products.map((p) => (
                              <option key={p.id} value={p.id}>
                                {p.sku} — {p.name}
                              </option>
                            ))}
                          </select>
                        </div>

                        <div className="col-span-2">
                          <label className="block text-[11px] text-slate-400 mb-1">
                            BOM Rev
                          </label>
                          <input
                            type="number"
                            min={1}
                            value={line.revision}
                            onChange={(e) => {
                              const rev = Math.max(1, Number(e.target.value));
                              setDraftItems((prev) =>
                                prev.map((item, i) =>
                                  i === idx ? { ...item, revision: rev } : item
                                )
                              );
                            }}
                            className="w-full bg-slate-900 border border-slate-700 rounded-md px-2.5 py-1.5 text-sm"
                          />
                        </div>

                        <div className="col-span-3">
                          <label className="block text-[11px] text-slate-400 mb-1">
                            Units (Tasks)
                          </label>
                          <input
                            type="number"
                            min={1}
                            max={100}
                            value={line.quantity}
                            onChange={(e) => {
                              const qty = Math.max(1, Number(e.target.value));
                              setDraftItems((prev) =>
                                prev.map((item, i) =>
                                  i === idx ? { ...item, quantity: qty } : item
                                )
                              );
                            }}
                            className="w-full bg-slate-900 border border-slate-700 rounded-md px-2.5 py-1.5 text-sm"
                          />
                        </div>

                        <div className="col-span-1 flex justify-end pt-4">
                          <button
                            type="button"
                            onClick={() =>
                              setDraftItems((prev) =>
                                prev.filter((_, i) => i !== idx)
                              )
                            }
                            className="p-1.5 text-slate-500 hover:text-rose-400 transition"
                          >
                            <Trash2 className="w-4 h-4" />
                          </button>
                        </div>
                      </div>
                    ))}

                    {createError && (
                      <p className="text-xs text-rose-400">
                        {createError.message}
                      </p>
                    )}

                    <div className="flex justify-end pt-2">
                      <button
                        type="submit"
                        disabled={creatingOrder}
                        className="px-4 py-2 rounded-lg bg-sky-600 hover:bg-sky-500 disabled:opacity-50 text-sm font-medium text-white flex items-center gap-2 transition"
                      >
                        {creatingOrder && (
                          <Loader2 className="w-4 h-4 animate-spin" />
                        )}
                        Submit Manufacturing Order
                      </button>
                    </div>
                  </form>
                )}
              </section>

              {/* Orders List */}
              <section className="space-y-4">
                <div className="flex items-center justify-between">
                  <h2 className="text-base font-semibold">
                    Orders & Unit Production Progress
                  </h2>
                  {loading && (
                    <span className="text-xs text-slate-400 flex items-center gap-1.5">
                      <Loader2 className="w-3.5 h-3.5 animate-spin" />
                      Syncing...
                    </span>
                  )}
                </div>

                {orders.length === 0 ? (
                  <div className="rounded-xl border border-slate-800 bg-slate-900/30 p-8 text-center text-slate-500 text-sm">
                    No manufacturing orders yet.
                  </div>
                ) : (
                  orders.map((order) => {
                    const badge =
                      STATUS_BADGE[order.status] ?? STATUS_BADGE.CREATED;
                    return (
                      <div
                        key={order.id}
                        className="rounded-xl border border-slate-800 bg-slate-900/50 p-5 space-y-4"
                      >
                        <div className="flex flex-wrap items-center justify-between gap-2">
                          <div>
                            <div className="flex items-center gap-2.5">
                              <span className="font-mono text-sm font-semibold text-slate-200">
                                #{order.id.slice(0, 8)}
                              </span>
                              <span
                                className={`inline-flex items-center gap-1.5 px-2.5 py-0.5 rounded-full text-xs font-medium border ${badge.classes}`}
                              >
                                {badge.icon}
                                {badge.label}
                              </span>
                            </div>
                            <p className="text-xs text-slate-500 font-mono mt-0.5">
                              {order.id}
                            </p>
                          </div>

                          <div className="text-right">
                            <div className="text-sm font-semibold text-slate-200">
                              {order.completedTasksCount} /{' '}
                              {order.totalTasksCount} units (
                              {order.progressPercentage}%)
                            </div>
                            <div className="text-xs text-slate-400">
                              {order.inProgressTasksCount} currently in
                              production
                            </div>
                          </div>
                        </div>

                        {/* Overall Order Progress Bar */}
                        <div className="w-full h-2.5 bg-slate-950 rounded-full overflow-hidden border border-slate-800">
                          <div
                            className={`h-full transition-all duration-300 ${
                              order.status === 'ERROR'
                                ? 'bg-rose-500'
                                : order.status === 'DONE'
                                  ? 'bg-emerald-500'
                                  : 'bg-sky-500'
                            }`}
                            style={{
                              width: `${Math.min(100, order.progressPercentage)}%`,
                            }}
                          />
                        </div>

                        {order.errorMessage && (
                          <div className="p-3 rounded-lg bg-rose-950/40 border border-rose-800/60 text-xs text-rose-300">
                            {order.errorMessage}
                          </div>
                        )}

                        {/* Order Items Breakdown */}
                        <div className="space-y-3 pt-1">
                          {order.items.map((item, idx) => {
                            const itemPct =
                              item.quantity > 0
                                ? Math.round(
                                    (item.completedQuantity / item.quantity) *
                                      100
                                  )
                                : 0;
                            return (
                              <div
                                key={idx}
                                className="p-3.5 rounded-lg bg-slate-950/70 border border-slate-800/80 space-y-2.5"
                              >
                                <div className="flex items-center justify-between text-xs">
                                  <div>
                                    <span className="font-semibold text-slate-200">
                                      {item.product?.name ?? item.productId}
                                    </span>{' '}
                                    <span className="text-slate-400 font-mono">
                                      ({item.product?.sku ?? 'SKU'} • Rev{' '}
                                      {item.revision})
                                    </span>
                                  </div>
                                  <span className="font-mono text-slate-300">
                                    {item.completedQuantity}/{item.quantity}{' '}
                                    completed ({itemPct}%)
                                  </span>
                                </div>

                                {/* Per-unit task pills */}
                                <div className="flex flex-wrap gap-1.5">
                                  {item.tasks.map((t, tIdx) => {
                                    const pillColor =
                                      t.status === 'COMPLETED'
                                        ? 'bg-emerald-500/20 border-emerald-500/50 text-emerald-300'
                                        : t.status === 'IN_PROGRESS'
                                          ? 'bg-sky-500/20 border-sky-500/50 text-sky-300 animate-pulse'
                                          : t.status === 'FAILED'
                                            ? 'bg-rose-500/20 border-rose-500/50 text-rose-300'
                                            : 'bg-slate-900 border-slate-800 text-slate-400';
                                    return (
                                      <span
                                        key={t.taskId}
                                        title={`Task ${t.taskId}: ${t.status}${t.lastMessage ? ` (${t.lastMessage})` : ''}`}
                                        className={`px-2 py-0.5 rounded text-[11px] font-mono border ${pillColor}`}
                                      >
                                        Unit #{tIdx + 1}: {t.status}
                                      </span>
                                    );
                                  })}
                                </div>
                              </div>
                            );
                          })}
                        </div>
                      </div>
                    );
                  })
                )}
              </section>
            </div>

            {/* Right column: Live Producer Event Stream */}
            <div className="space-y-4">
              <div className="rounded-xl border border-slate-800 bg-slate-900/50 p-5">
                <div className="flex items-center justify-between mb-4">
                  <h2 className="text-sm font-semibold flex items-center gap-2">
                    <Radio className="w-4 h-4 text-emerald-400 animate-pulse" />
                    Live NATS Producer Stream
                  </h2>
                  <span className="text-[11px] text-slate-400 font-mono">
                    ws://.../graphql
                  </span>
                </div>

                {eventFeed.length === 0 ? (
                  <p className="text-xs text-slate-500 py-8 text-center">
                    Listening for real-time ProducerEventV1 messages over
                    GraphQL WebSockets...
                  </p>
                ) : (
                  <div className="space-y-2.5 max-h-[540px] overflow-y-auto pr-1">
                    {eventFeed.map((evt, i) => (
                      <div
                        key={`${evt.taskId}-${i}`}
                        className="p-3 rounded-lg bg-slate-950/80 border border-slate-800 text-xs space-y-1"
                      >
                        <div className="flex items-center justify-between">
                          <span className="font-mono font-semibold text-sky-400">
                            {evt.type}
                          </span>
                          <span className="text-[10px] text-slate-500 font-mono">
                            {evt.timestamp}
                          </span>
                        </div>
                        <p className="text-slate-300">{evt.message}</p>
                        <p className="text-[10px] text-slate-500 font-mono truncate">
                          Task: {evt.taskId}
                        </p>
                      </div>
                    ))}
                  </div>
                )}
              </div>
            </div>
          </div>
        ) : (
          /* Catalog & BOMs Tab */
          <div className="space-y-8">
            <section className="space-y-4">
              <h2 className="text-base font-semibold">
                Products & Bill of Materials (Revision 1)
              </h2>
              <div className="grid grid-cols-1 md:grid-cols-2 gap-4">
                {products.map((product) => (
                  <div
                    key={product.id}
                    className="rounded-xl border border-slate-800 bg-slate-900/50 p-5 space-y-3"
                  >
                    <div className="flex items-start justify-between">
                      <div>
                        <h3 className="font-semibold text-slate-100">
                          {product.name}
                        </h3>
                        <p className="text-xs font-mono text-sky-400">
                          {product.sku}
                        </p>
                      </div>
                      <span className="text-xs px-2 py-0.5 rounded bg-slate-800 text-slate-300 font-mono">
                        Rev {product.billOfMaterial?.revision ?? 1}
                      </span>
                    </div>

                    <div className="border-t border-slate-800 pt-3">
                      <p className="text-xs font-medium text-slate-400 mb-2">
                        BOM Components (
                        {product.billOfMaterial?.materials.length ?? 0})
                      </p>
                      <div className="space-y-1.5 max-h-48 overflow-y-auto pr-1">
                        {product.billOfMaterial?.materials.map((item) => (
                          <div
                            key={item.materialId}
                            className="flex items-center justify-between text-xs bg-slate-950/60 px-2.5 py-1.5 rounded border border-slate-800/70"
                          >
                            <span className="text-slate-300 truncate pr-2">
                              {item.material?.name ?? item.materialId}
                              <span className="text-slate-500 font-mono ml-1.5">
                                ({item.material?.sku})
                              </span>
                            </span>
                            <span className="font-mono text-slate-200 shrink-0">
                              {item.quantity}{' '}
                              {item.material?.measurementUnit.toLowerCase()}
                            </span>
                          </div>
                        ))}
                      </div>
                    </div>
                  </div>
                ))}
              </div>
            </section>

            <section className="space-y-3">
              <h2 className="text-base font-semibold">
                Materials Catalog ({materials.length})
              </h2>
              <div className="rounded-xl border border-slate-800 bg-slate-900/50 overflow-hidden">
                <div className="max-h-96 overflow-y-auto">
                  <table className="w-full text-left text-xs">
                    <thead className="bg-slate-900 text-slate-400 sticky top-0">
                      <tr>
                        <th className="py-2.5 px-4 font-medium">SKU</th>
                        <th className="py-2.5 px-4 font-medium">Name</th>
                        <th className="py-2.5 px-4 font-medium">Unit</th>
                        <th className="py-2.5 px-4 font-medium">ID</th>
                      </tr>
                    </thead>
                    <tbody className="divide-y divide-slate-800/70">
                      {materials.slice(0, 100).map((m) => (
                        <tr key={m.id} className="hover:bg-slate-900/40">
                          <td className="py-2 px-4 font-mono text-sky-400">
                            {m.sku}
                          </td>
                          <td className="py-2 px-4 text-slate-200">{m.name}</td>
                          <td className="py-2 px-4 font-mono text-slate-400">
                            {m.measurementUnit}
                          </td>
                          <td className="py-2 px-4 font-mono text-slate-500">
                            {m.id}
                          </td>
                        </tr>
                      ))}
                    </tbody>
                  </table>
                </div>
              </div>
            </section>
          </div>
        )}
      </main>
    </div>
  );
}
